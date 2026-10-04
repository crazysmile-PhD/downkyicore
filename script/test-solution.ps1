[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$ExcludeCiInfrastructure,
    [string]$ResultsDirectory,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")

$excludedClassNames = @()
if ($ExcludeCiInfrastructure) {
    . (Join-Path $PSScriptRoot "ci-infrastructure-test-slices.ps1")
    $excludedClassNames = @(Get-DownKyiCiInfrastructureTestClassNames)
}

$result = Invoke-DownKyiTestSolution `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory `
    -ExcludedClassNames $excludedClassNames `
    -EvidenceDirectory $EvidenceDirectory
exit $result.ExitCode
