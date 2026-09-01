[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [ValidateRange(1, 3600)]
    [int]$ExecutionTimeoutSeconds = 300,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")

$result = Invoke-DownKyiTestSolution `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory `
    -ExecutionTimeoutSeconds $ExecutionTimeoutSeconds `
    -EvidenceDirectory $EvidenceDirectory
if ($result.ExitCode -ne 0) {
    throw "CentralTestRunner failed with exit code $($result.ExitCode)."
}
