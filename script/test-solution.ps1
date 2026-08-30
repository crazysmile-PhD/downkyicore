[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [ValidateRange(0, 63)]
    [int]$ShardIndex = 0,
    [ValidateRange(1, 64)]
    [int]$ShardCount = 1,
    [ValidateRange(1, 8)]
    [int]$MaxParallelProjects = 2,
    [string]$EvidencePath,
    [string]$ExpectedCommitSha
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "delegated-cgroup-scope.ps1")
if (Test-DownKyiDelegatedCgroupScopeRequired) {
    Invoke-DownKyiDelegatedCgroupScope `
        -ScriptPath $PSCommandPath `
        -ArgumentList (ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters)
    return
}

. (Join-Path $PSScriptRoot "test-project-runner.ps1")
Import-DownKyiCentralTestRunner `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -BuildIfMissing `
    -NoRestore:$NoRestore
& (Join-Path $PSScriptRoot "test-platform-selector.ps1")
$result = Invoke-DownKyiTestSolution `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory `
    -ShardIndex $ShardIndex `
    -ShardCount $ShardCount `
    -MaxParallelProjects $MaxParallelProjects

if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    . (Join-Path $PSScriptRoot "ci-evidence.ps1")
    Write-DownKyiRepositorySuiteEvidence `
        -RepositoryRoot $repositoryRoot `
        -ExpectedCommitSha $ExpectedCommitSha `
        -EvidencePath $EvidencePath `
        -SolutionResult $result
}
