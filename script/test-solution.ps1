[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
& (Join-Path $PSScriptRoot "test-platform-selector.ps1")
$null = Invoke-DownKyiTestSolution `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory
