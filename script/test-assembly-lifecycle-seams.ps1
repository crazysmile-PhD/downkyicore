[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ResultsDirectory = "artifacts/test-results/lifecycle-seams",
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$componentRoot = Join-Path $PSScriptRoot "assembly-lifecycle"
$lifecycleScripts = @(
    (Join-Path $PSScriptRoot "test-assembly-lifecycle.ps1")
    (Join-Path $componentRoot "forensics.ps1")
    (Join-Path $componentRoot "process-execution.ps1")
    (Join-Path $componentRoot "result-classification.ps1")
    (Join-Path $componentRoot "report-rendering.ps1")
)
foreach ($scriptPath in $lifecycleScripts) {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        throw "Lifecycle script failed to parse: $scriptPath"
    }
}

. (Join-Path $componentRoot "forensics.ps1")
. (Join-Path $componentRoot "result-classification.ps1")
$markerReaderProof = New-MarkerReaderSelfTestState -Required $false
$markerReaderProof.executed = $true
$parsedMarker = [pscustomobject]@{
    started = [pscustomobject]@{ processId = 123 }
    disposing = [pscustomobject]@{ processId = 123 }
    disposed = [pscustomobject]@{ processId = 123 }
}
Set-MarkerReaderSelfTestObservations `
    -SelfTest $markerReaderProof `
    -LockedMarker $null `
    -UnlockedMarker $parsedMarker `
    -ContentionCount 1
$markerReaderClassification =
    Complete-MarkerReaderSelfTestClassification -SelfTest $markerReaderProof
if (-not $markerReaderClassification.complete) {
    throw "Marker-reader classification seam did not accept a valid proof."
}

. (Join-Path $PSScriptRoot "test-project-runner.ps1")

$runnerArguments = @{
    RepositoryRoot = $repositoryRoot
    ProjectPath = Join-Path $repositoryRoot (
        "tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj")
    Configuration = $Configuration
    ClassNames = @(
        "DownKyi.Architecture.Tests.AssemblyLifecycleArchitectureTests")
    ResultsDirectory = [System.IO.Path]::GetFullPath(
        $ResultsDirectory,
        $repositoryRoot)
    TrxName = "AssemblyLifecycleArchitectureTests.trx"
}
if ($NoRestore) {
    $runnerArguments.NoRestore = $true
}
if ($NoBuild) {
    $runnerArguments.NoBuild = $true
}

$result = Invoke-DownKyiTestProject @runnerArguments
if ($result.ExitCode -ne 0) {
    throw "Assembly lifecycle seam validation failed with exit code $($result.ExitCode)."
}

Write-Host "Assembly lifecycle seam validation passed."
