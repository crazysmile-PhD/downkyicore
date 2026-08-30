[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Project", "Solution")]
    [string]$Mode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "delegated-cgroup-scope.ps1")

function ConvertFrom-DownKyiActionBoolean {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Value
    )

    if ([string]::Equals($Value, "true", [StringComparison]::Ordinal)) {
        return $true
    }
    if ([string]::Equals($Value, "false", [StringComparison]::Ordinal)) {
        return $false
    }

    throw "Action input '$Name' must be exactly 'true' or 'false'."
}

$actionRepositoryRoot = Split-Path -Parent $PSScriptRoot

if (Test-DownKyiDelegatedCgroupScopeRequired) {
    Invoke-DownKyiDelegatedCgroupScope `
        -ScriptPath $PSCommandPath `
        -ArgumentList @("-Mode", $Mode)
    return
}

if ($Mode -eq "Solution") {
    $solutionParameters = @{
        Configuration = "Release"
        NoRestore = $true
        NoBuild = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_RESULTS_DIRECTORY)) {
        $solutionParameters.ResultsDirectory = $env:DOWNKYI_TEST_RESULTS_DIRECTORY
    }

    & (Join-Path $PSScriptRoot "test-solution.ps1") @solutionParameters
    return
}

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_REPOSITORY_ROOT)) {
    $actionRepositoryRoot
}
else {
    (Resolve-Path -LiteralPath $env:DOWNKYI_TEST_REPOSITORY_ROOT).Path
}

. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$projectParameters = @{
    RepositoryRoot = $repositoryRoot
    ProjectPath = $env:DOWNKYI_TEST_PROJECT_PATH
    Configuration = $env:DOWNKYI_TEST_CONFIGURATION
}
if (ConvertFrom-DownKyiActionBoolean `
        -Name "no-restore" `
        -Value $env:DOWNKYI_TEST_NO_RESTORE) {
    $projectParameters.NoRestore = $true
}
if (ConvertFrom-DownKyiActionBoolean `
        -Name "no-build" `
        -Value $env:DOWNKYI_TEST_NO_BUILD) {
    $projectParameters.NoBuild = $true
}
if (-not [string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_RESULTS_DIRECTORY)) {
    $projectParameters.ResultsDirectory = $env:DOWNKYI_TEST_RESULTS_DIRECTORY
}
if (-not [string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_TRX_NAME)) {
    $projectParameters.TrxName = $env:DOWNKYI_TEST_TRX_NAME
}
if (-not [string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_EXPECTED_CLASS)) {
    $projectParameters.ClassNames = @($env:DOWNKYI_TEST_EXPECTED_CLASS)
}

$result = Invoke-DownKyiTestProject @projectParameters
if ([string]::IsNullOrWhiteSpace($env:DOWNKYI_TEST_EXPECTED_CLASS)) {
    if ($result.ExitCode -ne 0) {
        throw "Repository test project failed."
    }
}
else {
    Assert-DownKyiExpectedTestExecutionReport `
        -RunnerExitCode $result.ExitCode `
        -Report $result.Report `
        -ExpectedClassNames @($env:DOWNKYI_TEST_EXPECTED_CLASS)
}
