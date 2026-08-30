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
    "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj",
    "tests/$Assembly/$Assembly.csproj")
foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Lifecycle release shard project is missing: $project"
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
    $buildArguments = @(
        "build",
        (Join-Path $repositoryRoot $project)) + $strictBuildArguments
    Invoke-DotNetChecked -Arguments $buildArguments
}

& (Join-Path $PSScriptRoot "invoke-assembly-lifecycle-release-shard.ps1") `
    -Assembly $Assembly `
    -ShardIndex $ShardIndex `
    -ExpectedCommitSha $ExpectedCommitSha `
    -ResultsDirectory $ResultsDirectory
