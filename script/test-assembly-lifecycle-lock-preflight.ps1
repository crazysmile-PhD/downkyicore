[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommitSha,
    [Parameter(Mandatory)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedCommit = $ExpectedCommitSha.ToLowerInvariant()
$observedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $observedCommit -ne $expectedCommit) {
    throw "Lifecycle lock preflight must run at exact commit $expectedCommit; observed $observedCommit."
}
if (@(& git -C $repositoryRoot status --porcelain).Count -ne 0) {
    throw "Lifecycle lock preflight requires a clean exact-commit checkout."
}

$topologyPath = Join-Path $repositoryRoot "docs/testing/assembly-lifecycle-release-topology.json"
$topology = Get-Content -LiteralPath $topologyPath -Raw | ConvertFrom-Json -Depth 10
$assemblies = @($topology.preflightAssemblies)
if ([int]$topology.schemaVersion -ne 1 -or
    $topology.profile -ne "Rehearsal" -or
    [int]$topology.totalIterations -ne 100 -or
    $assemblies.Count -ne 2 -or
    $assemblies[0] -ne "DownKyi.Architecture.Tests" -or
    $assemblies[1] -ne "DownKyi.Windows.Tests") {
    throw "Lifecycle lock preflight topology is invalid."
}

$env:MSBUILDDISABLENODEREUSE = "1"
function Invoke-DotNetChecked {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$projects = @(
    "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj",
    "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj") +
    @($assemblies | ForEach-Object { "tests/$_/$_.csproj" })
foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Lifecycle lock preflight project is missing: $project"
    }
    Invoke-DotNetChecked -Arguments @("restore", $projectPath)
}

$strictBuildArguments = @(
    "-c", "Release",
    "--no-restore",
    "--no-incremental",
    "-p:TreatWarningsAsErrors=true",
    "-p:CodeAnalysisTreatWarningsAsErrors=true",
    "-p:EnableNETAnalyzers=true",
    "-p:AnalysisMode=All",
    "-p:EnforceCodeStyleInBuild=true",
    "-p:UseSharedCompilation=false")
foreach ($project in $projects) {
    $buildArguments = @("build", (Join-Path $repositoryRoot $project)) +
        $strictBuildArguments
    Invoke-DotNetChecked -Arguments $buildArguments
}

$patterns = @($assemblies | ForEach-Object { "^$([Regex]::Escape($_))$" })
$resultRoot = [IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
& (Join-Path $PSScriptRoot "test-assembly-lifecycle.ps1") `
    -Configuration Release `
    -Profile Local `
    -NoBuild `
    -ValidateForensics `
    -AssemblyPattern $patterns `
    -ResultsDirectory $resultRoot

$reports = @(Get-ChildItem -LiteralPath $resultRoot -Recurse -File |
        Where-Object Name -eq "assembly-lifecycle-report.json")
if ($reports.Count -ne 1) {
    throw "Lifecycle lock preflight must produce exactly one machine report."
}
$report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json -Depth 30
$actualAssemblies = @(
    $report.results |
        Where-Object { $_.assembly -in $assemblies } |
        ForEach-Object assembly |
        Sort-Object -Unique)
if (-not [bool]$report.successful -or
    $report.profile -ne "Local" -or
    [int]$report.iterations -ne 1 -or
    $report.commitSha.ToLowerInvariant() -ne $expectedCommit -or
    [bool]$report.workingTreeDirty -or
    [int]$report.testAssemblyCount -ne 2 -or
    [int]$report.failedPhaseCount -ne 0 -or
    [int]$report.slowEvidenceMissingCount -ne 0 -or
    [int]$report.residualChildPhaseCount -ne 0 -or
    [string]::Join("|", $actualAssemblies) -ne [string]::Join("|", $assemblies)) {
    throw "Lifecycle lock preflight did not prove both exact assemblies."
}

Write-Host "Lifecycle lock preflight passed for $($assemblies -join ', ') at $expectedCommit."
