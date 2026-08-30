[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        "DownKyi.Application.Tests",
        "DownKyi.Architecture.Tests",
        "DownKyi.Core.Tests",
        "DownKyi.Desktop.Tests",
        "DownKyi.Domain.Tests",
        "DownKyi.Infrastructure.Tests",
        "DownKyi.Tests",
        "DownKyi.Windows.Tests")]
    [string]$Assembly,
    [Parameter(Mandatory)]
    [ValidateRange(0, 63)]
    [int]$ShardIndex,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommitSha,
    [Parameter(Mandatory)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$topologyPath = Join-Path $repositoryRoot "docs/testing/assembly-lifecycle-release-topology.json"
$topology = Get-Content -LiteralPath $topologyPath -Raw | ConvertFrom-Json -Depth 10
$assemblyTopology = @($topology.assemblies | Where-Object name -eq $Assembly)
if ([int]$topology.schemaVersion -ne 1 -or
    $topology.profile -ne "Rehearsal" -or
    [int]$topology.totalIterations -ne 100 -or
    $assemblyTopology.Count -ne 1) {
    throw "Lifecycle release topology is missing or invalid for $Assembly."
}
$shardCount = [int]$assemblyTopology[0].shardCount
if ($ShardIndex -ge $shardCount) {
    throw "Shard index must be less than the authoritative shard count."
}

$expectedCommit = $ExpectedCommitSha.ToLowerInvariant()
$observedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $observedCommit -ne $expectedCommit) {
    throw "Lifecycle release shard must run at exact commit $expectedCommit; observed $observedCommit."
}

$dirtyEntries = @(& git -C $repositoryRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirtyEntries.Count -ne 0) {
    throw "Lifecycle release shard requires a clean exact-commit checkout."
}

$totalIterations = [int]$topology.totalIterations
$baseIterations = [Math]::Floor($totalIterations / $shardCount)
$remainder = $totalIterations % $shardCount
$shardIterations = [int]$baseIterations
if ($ShardIndex -lt $remainder) {
    $shardIterations++
}
if ($shardIterations -lt 1) {
    throw "Shard $ShardIndex of $shardCount owns no Rehearsal iterations."
}

$resultRoot = [IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$assemblyPattern = "^$([Regex]::Escape($Assembly))$"
& (Join-Path $PSScriptRoot "test-assembly-lifecycle.ps1") `
    -Configuration Release `
    -Profile Rehearsal `
    -Iterations $shardIterations `
    -NoBuild `
    -ValidateForensics `
    -AssemblyPattern $assemblyPattern `
    -ResultsDirectory $resultRoot

$reports = @(Get-ChildItem -LiteralPath $resultRoot -Recurse -File |
        Where-Object Name -eq "assembly-lifecycle-report.json")
if ($reports.Count -ne 1) {
    throw "Lifecycle shard must produce exactly one machine report; observed $($reports.Count)."
}

$report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json -Depth 20
if (-not $report.successful -or
    $report.profile -ne "Rehearsal" -or
    [int]$report.iterations -ne $shardIterations -or
    $report.commitSha.ToLowerInvariant() -ne $expectedCommit -or
    [bool]$report.workingTreeDirty -or
    [int]$report.testAssemblyCount -ne 1) {
    throw "Lifecycle shard report does not match its exact Rehearsal authority."
}

$relativeReportPath = [IO.Path]::GetRelativePath(
    $resultRoot,
    $reports[0].FullName).Replace('\', '/')
$reportHash = (Get-FileHash -LiteralPath $reports[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    kind = "assembly-lifecycle-release-shard"
    evidenceId = "$Assembly/$ShardIndex-of-$shardCount"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    commitSha = $observedCommit
    assembly = $Assembly
    profile = "Rehearsal"
    validateForensics = $true
    shardIndex = $ShardIndex
    shardCount = $shardCount
    totalIterations = $totalIterations
    shardIterations = $shardIterations
    reportRelativePath = $relativeReportPath
    reportSha256 = $reportHash
    successful = $true
}
$manifestPath = Join-Path $resultRoot "shard-manifest.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host (
    "Lifecycle release shard passed: assembly=$Assembly, " +
    "shard=$ShardIndex/$shardCount, iterations=$shardIterations, commit=$observedCommit.")
