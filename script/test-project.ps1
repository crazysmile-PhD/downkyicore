[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectPath,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [string]$TrxName,
    [string[]]$ClassNames = @(),
    [string]$Filter,
    [ValidateRange(1, 3600)]
    [int]$ExecutionTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "delegated-cgroup-scope.ps1")
if (Test-DownKyiDelegatedCgroupScopeRequired) {
    Invoke-DownKyiDelegatedCgroupScope `
        -ScriptPath $PSCommandPath `
        -ArgumentList (ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters)
    return
}

. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$result = Invoke-DownKyiTestProject `
    -RepositoryRoot $RepositoryRoot `
    -ProjectPath $ProjectPath `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory `
    -TrxName $TrxName `
    -ClassNames $ClassNames `
    -Filter $Filter `
    -ExecutionTimeoutSeconds $ExecutionTimeoutSeconds

if ($ClassNames.Count -gt 0) {
    $null = Assert-DownKyiExpectedTestExecution `
        -RunnerExitCode $result.ExitCode `
        -TrxPath $result.TrxPath `
        -ExpectedClassNames $ClassNames `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration
}
elseif ($result.ExitCode -ne 0) {
    throw "Repository test project failed."
}

$result
