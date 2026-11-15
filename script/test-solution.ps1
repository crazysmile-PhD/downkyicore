[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$ProductOnly,
    [string]$ResultsDirectory,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")

$excludedClassNames = @()
if ($ProductOnly) {
    . (Join-Path $PSScriptRoot "ci-infrastructure-test-slices.ps1")
    $excludedClassNames = @(Get-DownKyiCiInfrastructureTestClassNames)
    $excludedClassNames += @(
        'DownKyi.Architecture.Tests.ReleaseSafetyRegressionTests'
        'DownKyi.Architecture.Tests.ReleasePublicationAssetTests'
        'DownKyi.Architecture.Tests.PublishedReleaseReadbackTests'
    )
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
