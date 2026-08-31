[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$processSupervisionAssembly = Join-Path $repositoryRoot (
    "tools/DownKyi.ProcessSupervision/bin/$Configuration/net10.0/" +
    "DownKyi.ProcessSupervision.dll")
if (-not (Test-Path -LiteralPath $processSupervisionAssembly -PathType Leaf)) {
    throw "Process supervision assembly was not built: $processSupervisionAssembly"
}

[Reflection.Assembly]::LoadFrom($processSupervisionAssembly) | Out-Null
$runId = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
$runRoot = Join-Path $repositoryRoot (
    "artifacts/test-results/single-process-owner/powershell-boundary/$runId")
$rawRoot = Join-Path $runRoot "raw"
$evidenceRoot = Join-Path $runRoot "evidence"
$PhaseTimeoutSeconds = 1
$SlowPhaseThresholdSeconds = 60
$slowEvidenceCaptureLeadMilliseconds = 1000
$processCleanupGraceSeconds = 4
$residualChildQuiescenceMilliseconds = 500
$residualChildPollMilliseconds = 25
$script:diagnosticsTool = $null
$script:markerReadContentionCount = 0
$script:markerReadRetriesExhaustedCount = 0
$script:markerReadErrorCount = 0
$script:markerReadErrorType = $null

$componentRoot = Join-Path $PSScriptRoot "assembly-lifecycle"
. (Join-Path $componentRoot "forensics.ps1")
. (Join-Path $componentRoot "process-execution.ps1")
New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

$readyPath = Join-Path $runRoot "owned-descendant-ready.json"
try {
    try {
        $result = Invoke-IsolatedProcess `
            -AssemblyName "PowerShellBoundary" `
            -Iteration 1 `
            -Phase "typed-failure" `
            -FileName "dotnet" `
            -Arguments @(
                $processSupervisionAssembly,
                "--exit-with-owned-descendant",
                $readyPath
            ) `
            -OperationTimeoutSeconds 1 `
            -EvidenceThresholdSeconds 60
    }
    catch {
        throw [InvalidOperationException]::new(
            "Invoke-IsolatedProcess propagated an unstructured lease failure.",
            $_.Exception)
    }

    if ($result -isnot [pscustomobject]) {
        throw "Invoke-IsolatedProcess did not return one structured result."
    }
    if ($result.ownedProcessFailureKind -cne "OwnedTreeNotQuiescent") {
        throw "Expected OwnedTreeNotQuiescent, got '$($result.ownedProcessFailureKind)'."
    }
    if ($result.ownedTreeQuiescent) {
        throw "The structured failure incorrectly reported a quiescent tree."
    }
    if ($null -eq $result.processOwnership -or
        -not $result.processOwnership.OwnershipEstablished) {
        throw "The structured failure lost its containment ownership authority."
    }
    if ($null -eq $result.processExitedAtUnixMs) {
        throw "The structured failure lost the authoritative target-exit observation."
    }
    if ($result.ownedProcessPrimaryFailure.type -cne "TimeoutException") {
        throw "The structured failure lost its primary TimeoutException cause."
    }
    $cleanupProjectionProperties = @(
        $result.PSObject.Properties |
            Where-Object Name -CEQ "ownedProcessCleanupFailures"
    )
    if ($cleanupProjectionProperties.Count -ne 1) {
        throw "The structured failure omitted its cleanup-failure projection."
    }
    if (@($result.ownedProcessCleanupFailures).Count -ne 0) {
        throw "The deterministic fixture unexpectedly produced cleanup failures."
    }

    Write-Host "PowerShell owned-process failure boundary passed."
}
finally {
    Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
}
