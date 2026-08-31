[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("Local", "PR", "Main", "Rehearsal", "Flaky")]
    [string]$Profile = "Local",
    [ValidateRange(0, 10000)]
    [int]$Iterations = 0,
    [ValidateRange(1, 3600)]
    [int]$PhaseTimeoutSeconds = 180,
    [ValidateRange(0.1, 60)]
    [double]$SlowPhaseThresholdSeconds = 5,
    [ValidateRange(0.01, 60)]
    [double]$ExitThresholdSeconds = 1,
    [string[]]$AssemblyPattern = @("*"),
    [string]$ResultsDirectory = "artifacts/assembly-lifecycle",
    [string]$DiagnosticsToolPath,
    [switch]$ValidateForensics,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$solutionPath = Join-Path $repositoryRoot "DownKyi.sln"
$probeProject = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj"
$probeAssembly = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/bin/$Configuration/net10.0/DownKyi.AssemblyLifecycleProbe.dll"
$profileIterations = @{
    Local = 1
    PR = 3
    Main = 5
    Rehearsal = 100
    Flaky = 500
}
$resolvedIterations = if ($Iterations -gt 0) {
    $Iterations
}
else {
    $profileIterations[$Profile]
}
$runId = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
$outputRoot = [System.IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
$runRoot = Join-Path $outputRoot $runId
$rawRoot = Join-Path $runRoot "raw"
$evidenceRoot = Join-Path $runRoot "evidence"
$ownershipRoot = Join-Path $runRoot "ownership"
$script:markerReadContentionCount = 0
$script:markerReadRetriesExhaustedCount = 0
$script:markerReadErrorCount = 0
$script:markerReadErrorType = $null
$slowEvidenceCaptureLeadMilliseconds = 1000
$residualChildQuiescenceMilliseconds = 500
$residualChildPollMilliseconds = 25
$forensicsSelfTestCaptureLeadValidated = $false
$markerReaderSelfTestRequired = $IsWindows -and
    @("PR", "Main", "Rehearsal", "Flaky").Contains($Profile)
$markerReaderSelfTestComplete = $false
$residualChildSelfTestComplete = $false
$residualChildSelfTest = [ordered]@{
    required = $IsWindows -and $ValidateForensics
    executed = $false
    passed = $false
    childObserved = $false
    identityCaptured = $false
    evidenceManifestWritten = $false
    failureClassified = $false
    transientChildObserved = $false
    transientChildDrained = $false
    transientPhasePassed = $false
    cleanupCompleted = $false
    redactionValidated = $false
    observedChildCount = 0
    errorType = $null
}
$markerReaderSelfTest = [ordered]@{
    required = $markerReaderSelfTestRequired
    executed = $false
    passed = $false
    contentionObserved = $false
    contentionCount = 0
    recoveredAfterLockRelease = $false
    markerParsedAfterRecovery = $false
    errorType = $null
    contractChecks = [ordered]@{
        executed = $false
        passed = $false
        validProofAccepted = $false
        errorTypeRejected = $false
        zeroContentionRejected = $false
        incompleteProofRejected = $false
        errorClassificationPassed = $false
    }
}

$componentRoot = Join-Path $PSScriptRoot "assembly-lifecycle"
. (Join-Path $componentRoot "forensics.ps1")
. (Join-Path $componentRoot "process-execution.ps1")
. (Join-Path $componentRoot "result-classification.ps1")
. (Join-Path $componentRoot "report-rendering.ps1")

New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

if ($markerReaderSelfTestRequired -and -not $ValidateForensics) {
    throw "Formal Windows lifecycle profiles require -ValidateForensics."
}


$reporterContractSelfTestPassed = Test-XunitReporterContractMutation
if (-not $reporterContractSelfTestPassed) {
    throw "xUnit reporter contract mutation self-test did not fail closed."
}

$script:diagnosticsTool = Resolve-DiagnosticsTool
$ownershipPassed = $true
$ownershipError = $null
try {
    & (Join-Path $PSScriptRoot "audit-lifecycle-ownership.ps1") `
        -OutputDirectory $ownershipRoot
}
catch {
    $ownershipPassed = $false
    $ownershipError = $_.Exception.GetType().Name
    Write-Warning "Lifecycle ownership audit failed; dynamic probing will continue."
}

if (-not $NoBuild) {
    & dotnet build $solutionPath `
        -c $Configuration `
        --no-incremental `
        -p:TreatWarningsAsErrors=true `
        -p:CodeAnalysisTreatWarningsAsErrors=true `
        -p:EnableNETAnalyzers=true `
        -p:AnalysisMode=All `
        -p:EnforceCodeStyleInBuild=true `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Strict solution build failed."
    }
}

if (-not (Test-Path -LiteralPath $probeAssembly -PathType Leaf)) {
    throw "Assembly lifecycle probe was not built: $probeAssembly"
}

$allTestProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests") `
        -Filter "*.Tests.csproj" `
        -File `
        -Recurse |
        Sort-Object BaseName
)
$currentPlatform = Get-DownKyiCurrentTestPlatform
$platformTestProjects = @(
    Select-DownKyiTestProjectsForCurrentPlatform `
        -Projects $allTestProjects `
        -CurrentPlatform $currentPlatform
)
$testProjects = @(
    $platformTestProjects |
        Where-Object {
            $project = $_
            @($AssemblyPattern | Where-Object { $project.BaseName -like $_ }).Count -gt 0
        }
)
if ($testProjects.Count -eq 0) {
    throw "No '$currentPlatform' xUnit test assemblies matched the requested patterns."
}

$phaseResults = @()
if ($ValidateForensics) {
    if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
        throw "Forensics validation requires dotnet-stack."
    }

    $selfTestAssembly = Join-Path $testProjects[0].DirectoryName (
        "bin/$Configuration/net10.0/$($testProjects[0].BaseName).dll")
    $selfTestMarker = Join-Path $rawRoot "Gate.Forensics/iteration-0001/execution.lifecycle"
    $selfTest = Invoke-IsolatedProcess `
        -AssemblyName "Gate.Forensics" `
        -Iteration 1 `
        -Phase "execution" `
        -FileName "dotnet" `
        -Arguments @(
            $probeAssembly,
            "--assembly",
            $selfTestAssembly,
            "--hold-after-unload-ms",
            "5000"
        ) `
        -LifecycleMarkerPath $selfTestMarker `
        -EvidenceThresholdSeconds 1.25
    $selfTestPhase = New-ProcessPhaseResult -ProcessResult $selfTest
    $evidenceReports = @(
        foreach ($relativeEvidence in $selfTest.evidence) {
            $evidencePath = Join-Path $runRoot $relativeEvidence "process-evidence.json"
            if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
                Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
            }
        }
    )
    $forensicsValid = $selfTestPhase.success -and
        $evidenceReports.Count -gt 0 -and
        @($evidenceReports | Where-Object { $_.managedStack.captured -eq $true }).Count -gt 0 -and
        $selfTest.slowEvidenceTriggeredBeforeThreshold
    $forensicsSelfTestCaptureLeadValidated =
        $selfTest.slowEvidenceTriggeredBeforeThreshold
    $phaseResults += [pscustomobject]@{
        assembly = "Gate.Forensics"
        iteration = 1
        phase = "forensics-self-test"
        processId = $selfTest.processId
        success = $forensicsValid
        failureType = if ($forensicsValid) { $null } else { "ForensicsSelfTestFailed" }
        errorType = $selfTestPhase.errorType
        exitCode = if ($forensicsValid) { 0 } else { 1 }
        durationMs = $selfTest.durationMs
        timedOut = $selfTest.timedOut
        stdoutPolluted = $selfTestPhase.stdoutPolluted
        stderrPolluted = $selfTestPhase.stderrPolluted
        unexpectedOutput = $selfTestPhase.unexpectedOutput
        observedChildCount = $selfTestPhase.observedChildCount
        observedChildren = @($selfTestPhase.observedChildren)
        transientChildCount = $selfTestPhase.transientChildCount
        transientChildren = @($selfTestPhase.transientChildren)
        residualChildCount = $selfTestPhase.residualChildCount
        residualChildren = @($selfTestPhase.residualChildren)
        childProcessObservationSampleCount =
            $selfTestPhase.childProcessObservationSampleCount
        childProcessObservationDurationMs =
            $selfTestPhase.childProcessObservationDurationMs
        residualChildEvidence = @($selfTestPhase.residualChildEvidence)
        residualChildEvidenceStatus = $selfTestPhase.residualChildEvidenceStatus
        residualChildEvidenceErrorType = $selfTestPhase.residualChildEvidenceErrorType
        stdoutPath = $selfTest.stdoutPath
        stderrPath = $selfTest.stderrPath
        evidence = $selfTest.evidence
        slowEvidence = $selfTest.slowEvidence
        exitEvidence = $selfTest.exitEvidence
        timeoutEvidence = $selfTest.timeoutEvidence
        diagnosticCaptureDurationMs = $selfTest.diagnosticCaptureDurationMs
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold =
            $selfTest.slowEvidenceTriggeredBeforeThreshold
    }

    if ($IsWindows) {
        $residualChildSelfTestStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $residualChildSelfTest.executed = $true
        $residualProbe = $null
        $residualProbePhase = $null
        $transientProbe = $null
        $transientProbePhase = $null
        $observedResidualChildren = @()
        try {
            $residualProbe = Invoke-IsolatedProcess `
                -AssemblyName "Gate.ResidualChild" `
                -Iteration 1 `
                -Phase "residual-child-probe" `
                -FileName "dotnet" `
                -Arguments @(
                    $probeAssembly,
                    "--spawn-residual-child-ms",
                    "20000"
                )
            $residualProbePhase = New-ProcessPhaseResult -ProcessResult $residualProbe
            $residualPayload = $residualProbe.stdout | ConvertFrom-Json -ErrorAction Stop
            $expectedChildProcessId = [int]$residualPayload.ChildProcessId
            $observedResidualChildren = @($residualProbe.residualChildren)
            $matchingChild = @(
                $observedResidualChildren |
                    Where-Object processId -eq $expectedChildProcessId
            )
            $residualChildSelfTest.observedChildCount =
                $observedResidualChildren.Count
            $residualChildSelfTest.childObserved = $matchingChild.Count -eq 1
            $residualChildSelfTest.identityCaptured =
                $matchingChild.Count -eq 1 -and
                -not [string]::IsNullOrWhiteSpace($matchingChild[0].name) -and
                -not [string]::IsNullOrWhiteSpace($matchingChild[0].createdAtUtc)
            $residualChildSelfTest.evidenceManifestWritten =
                $residualProbe.residualChildEvidenceStatus -eq "captured" -and
                @(
                    foreach ($relativePath in $residualProbe.residualChildEvidence) {
                        $manifestPath = Join-Path $runRoot $relativePath (
                            "residual-children.json")
                        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
                            $manifestPath
                        }
                    }
                ).Count -gt 0
            $residualChildSelfTest.failureClassified =
                -not $residualProbePhase.success -and
                $residualProbePhase.failureType -eq "ResidualChildProcess"

            $transientProbe = Invoke-IsolatedProcess `
                -AssemblyName "Gate.TransientChild" `
                -Iteration 1 `
                -Phase "transient-child-probe" `
                -FileName "dotnet" `
                -Arguments @(
                    $probeAssembly,
                    "--spawn-residual-child-ms",
                    "250"
                )
            $transientProbePhase = New-ProcessPhaseResult `
                -ProcessResult $transientProbe
            $transientPayload = $transientProbe.stdout |
                ConvertFrom-Json -ErrorAction Stop
            $expectedTransientProcessId = [int]$transientPayload.ChildProcessId
            $matchingTransientObservation = @(
                $transientProbe.observedChildren |
                    Where-Object processId -eq $expectedTransientProcessId
            )
            $matchingTransientDrain = @(
                $transientProbe.transientChildren |
                    Where-Object processId -eq $expectedTransientProcessId
            )
            $matchingTransientResidual = @(
                $transientProbe.residualChildren |
                    Where-Object processId -eq $expectedTransientProcessId
            )
            $residualChildSelfTest.transientChildObserved =
                $matchingTransientObservation.Count -eq 1
            $residualChildSelfTest.transientChildDrained =
                $matchingTransientDrain.Count -eq 1 -and
                $matchingTransientResidual.Count -eq 0
            $residualChildSelfTest.transientPhasePassed =
                $transientProbePhase.success
            $redactionSample = (
                "$repositoryRoot https://example.invalid/private " +
                "SESSDATA=example-cookie-value " +
                "--rpc-secret `"example secret value`"")
            $redactedSample = Protect-ProcessDiagnosticText -Value $redactionSample
            $residualChildSelfTest.redactionValidated =
                $redactedSample.Contains(
                    "<repository>",
                    [StringComparison]::Ordinal) -and
                $redactedSample.Contains("<url>", [StringComparison]::Ordinal) -and
                $redactedSample.Contains(
                    "SESSDATA=<redacted>",
                    [StringComparison]::Ordinal) -and
                $redactedSample.Contains(
                    "--rpc-secret <redacted>",
                    [StringComparison]::Ordinal) -and
                -not $redactedSample.Contains(
                    "example-cookie-value",
                    [StringComparison]::Ordinal) -and
                -not $redactedSample.Contains(
                    "example secret value",
                    [StringComparison]::Ordinal)
        }
        catch {
            $residualChildSelfTest.errorType = $_.Exception.GetType().Name
        }
        finally {
            $cleanupCompleted = $true
            foreach ($child in $observedResidualChildren) {
                $childProcess = $null
                try {
                    $childProcess = Get-Process `
                        -Id $child.processId `
                        -ErrorAction SilentlyContinue
                    if ($null -eq $childProcess) {
                        continue
                    }

                    $actualStart = [DateTimeOffset]$childProcess.StartTime.ToUniversalTime()
                    $expectedStart = [DateTimeOffset]::Parse(
                        $child.createdAtUtc,
                        [System.Globalization.CultureInfo]::InvariantCulture)
                    if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1) {
                        $cleanupCompleted = $false
                        continue
                    }

                    $childProcess.Kill($true)
                    if (-not $childProcess.WaitForExit(5000)) {
                        $cleanupCompleted = $false
                    }
                }
                catch {
                    $cleanupCompleted = $false
                    if ($null -eq $residualChildSelfTest.errorType) {
                        $residualChildSelfTest.errorType =
                            $_.Exception.GetType().Name
                    }
                }
                finally {
                    if ($null -ne $childProcess) {
                        $childProcess.Dispose()
                    }
                }
            }

            $residualChildSelfTest.cleanupCompleted = $cleanupCompleted
            $residualChildSelfTestStopwatch.Stop()
        }

        $residualChildSelfTest.passed =
            $residualChildSelfTest.childObserved -and
            $residualChildSelfTest.identityCaptured -and
            $residualChildSelfTest.evidenceManifestWritten -and
            $residualChildSelfTest.failureClassified -and
            $residualChildSelfTest.transientChildObserved -and
            $residualChildSelfTest.transientChildDrained -and
            $residualChildSelfTest.transientPhasePassed -and
            $residualChildSelfTest.cleanupCompleted -and
            $residualChildSelfTest.redactionValidated -and
            $null -eq $residualChildSelfTest.errorType
        $residualChildSelfTestComplete = $residualChildSelfTest.passed
        if (-not $residualChildSelfTestComplete -and
            $null -eq $residualChildSelfTest.errorType) {
            $residualChildSelfTest.errorType = "ContractNotSatisfied"
        }

        $phaseResults += [pscustomobject]@{
            assembly = "Gate.ResidualChild"
            iteration = 1
            phase = "residual-child-self-test"
            processId = if ($null -eq $residualProbe) {
                $PID
            }
            else {
                $residualProbe.processId
            }
            success = $residualChildSelfTestComplete
            failureType = if ($residualChildSelfTestComplete) {
                $null
            }
            else {
                "ResidualChildSelfTestFailed"
            }
            errorType = $residualChildSelfTest.errorType
            exitCode = if ($residualChildSelfTestComplete) { 0 } else { 1 }
            durationMs = [Math]::Round(
                $residualChildSelfTestStopwatch.Elapsed.TotalMilliseconds,
                3)
            timedOut = $false
            stdoutPolluted = $false
            stderrPolluted = $false
            unexpectedOutput = @()
            observedChildCount = 0
            observedChildren = @()
            transientChildCount = 0
            transientChildren = @()
            residualChildCount = 0
            residualChildren = @()
            childProcessObservationSampleCount = 0
            childProcessObservationDurationMs = 0.0
            residualChildEvidence = @(
                if ($null -ne $residualProbe) {
                    $residualProbe.residualChildEvidence
                }
            )
            residualChildEvidenceStatus = if ($null -eq $residualProbe) {
                "not-triggered"
            }
            else {
                $residualProbe.residualChildEvidenceStatus
            }
            residualChildEvidenceErrorType = if ($null -eq $residualProbe) {
                $null
            }
            else {
                $residualProbe.residualChildEvidenceErrorType
            }
            stdoutPath = if ($null -eq $residualProbe) {
                $null
            }
            else {
                $residualProbe.stdoutPath
            }
            stderrPath = if ($null -eq $residualProbe) {
                $null
            }
            else {
                $residualProbe.stderrPath
            }
            evidence = @(
                if ($null -ne $residualProbe) {
                    $residualProbe.evidence
                }
            )
            slowEvidence = @()
            exitEvidence = @()
            timeoutEvidence = @()
            diagnosticCaptureDurationMs = if ($null -eq $residualProbe) {
                0.0
            }
            else {
                $residualProbe.diagnosticCaptureDurationMs
            }
            slowThresholdExceeded = $false
            slowEvidenceStatus = "not-applicable"
            slowEvidenceErrorType = $null
            slowEvidenceTriggeredBeforeThreshold = $false
        }

        $markerReaderSelfTestStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $markerReaderSelfTest.executed = $true
        $markerReaderTestPath = Join-Path $rawRoot "Gate.MarkerReader/read-race.lifecycle"
        $contentionBaseline = $script:markerReadContentionCount
        $lockedMarker = $null
        $exclusiveStream = $null
        try {
            New-Item -ItemType Directory -Force `
                -Path ([System.IO.Path]::GetDirectoryName($markerReaderTestPath)) |
                Out-Null
            @(
                "started|123|1000"
                "disposing|123|1001"
                "disposed|123|1002"
            ) | Set-Content -LiteralPath $markerReaderTestPath -Encoding utf8
            $exclusiveStream = [System.IO.FileStream]::new(
                $markerReaderTestPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            try {
                $lockedMarker = Read-TeardownMarker `
                    -Path $markerReaderTestPath `
                    -Attempts 2 `
                    -RetryDelayMilliseconds 1
            }
            finally {
                $exclusiveStream.Dispose()
                $exclusiveStream = $null
            }

            $markerReaderSelfTest.contentionCount =
                $script:markerReadContentionCount - $contentionBaseline
            $markerReaderSelfTest.contentionObserved =
                $markerReaderSelfTest.contentionCount -gt 0
            $unlockedMarker = Read-TeardownMarker -Path $markerReaderTestPath
            $markerReaderSelfTest.recoveredAfterLockRelease = $null -ne $unlockedMarker
            $markerReaderSelfTest.markerParsedAfterRecovery =
                $null -ne $unlockedMarker -and
                $null -ne $unlockedMarker.started -and
                $null -ne $unlockedMarker.disposing -and
                $null -ne $unlockedMarker.disposed
            $markerReaderSelfTest.passed =
                $null -eq $lockedMarker -and
                $markerReaderSelfTest.contentionObserved -and
                $markerReaderSelfTest.recoveredAfterLockRelease -and
                $markerReaderSelfTest.markerParsedAfterRecovery
        }
        catch {
            $markerReaderSelfTest.errorType = $_.Exception.GetType().Name
        }
        finally {
            if ($null -ne $exclusiveStream) {
                $exclusiveStream.Dispose()
            }

            $markerReaderSelfTestStopwatch.Stop()
        }

        if (-not $markerReaderSelfTest.passed -and
            $null -eq $markerReaderSelfTest.errorType) {
            $markerReaderSelfTest.errorType = "ContractNotSatisfied"
        }

        $validProof = [ordered]@{
            executed = $true
            passed = $true
            contentionObserved = $true
            contentionCount = 1
            recoveredAfterLockRelease = $true
            markerParsedAfterRecovery = $true
            errorType = $null
        }
        $proofWithError = [ordered]@{
            executed = $true
            passed = $true
            contentionObserved = $true
            contentionCount = 1
            recoveredAfterLockRelease = $true
            markerParsedAfterRecovery = $true
            errorType = "UnauthorizedAccessException"
        }
        $proofWithoutContention = [ordered]@{
            executed = $true
            passed = $true
            contentionObserved = $true
            contentionCount = 0
            recoveredAfterLockRelease = $true
            markerParsedAfterRecovery = $true
            errorType = $null
        }
        $incompleteProof = [ordered]@{
            executed = $true
            passed = $true
            contentionObserved = $true
            contentionCount = 1
            recoveredAfterLockRelease = $true
            markerParsedAfterRecovery = $false
            errorType = $null
        }
        $markerReaderSelfTest.contractChecks.executed = $true
        $markerReaderSelfTest.contractChecks.validProofAccepted =
            Test-MarkerReaderSelfTestProof -SelfTest $validProof
        $markerReaderSelfTest.contractChecks.errorTypeRejected =
            -not (Test-MarkerReaderSelfTestProof -SelfTest $proofWithError)
        $markerReaderSelfTest.contractChecks.zeroContentionRejected =
            -not (Test-MarkerReaderSelfTestProof -SelfTest $proofWithoutContention)
        $markerReaderSelfTest.contractChecks.incompleteProofRejected =
            -not (Test-MarkerReaderSelfTestProof -SelfTest $incompleteProof)
        $markerReaderSelfTest.contractChecks.errorClassificationPassed =
            (Get-LifecycleMarkerReadFailureCategory `
                -Exception ([System.IO.IOException]::new("generic"))) -eq "error" -and
            (Get-LifecycleMarkerReadFailureCategory `
                -Exception ([System.UnauthorizedAccessException]::new("denied"))) -eq "error"
        $markerReaderSelfTest.contractChecks.passed =
            $markerReaderSelfTest.contractChecks.validProofAccepted -and
            $markerReaderSelfTest.contractChecks.errorTypeRejected -and
            $markerReaderSelfTest.contractChecks.zeroContentionRejected -and
            $markerReaderSelfTest.contractChecks.incompleteProofRejected -and
            $markerReaderSelfTest.contractChecks.errorClassificationPassed
        $markerReaderSelfTestComplete =
            (Test-MarkerReaderSelfTestProof -SelfTest $markerReaderSelfTest) -and
            $markerReaderSelfTest.contractChecks.passed
        $markerReaderSelfTestFailureType = if ($markerReaderSelfTestComplete) {
            $null
        }
        elseif ($null -ne $markerReaderSelfTest.errorType) {
            $markerReaderSelfTest.errorType
        }
        else {
            "ContractChecksFailed"
        }

        $phaseResults += [pscustomobject]@{
            assembly = "Gate.MarkerReader"
            iteration = 1
            phase = "marker-reader-self-test"
            processId = $PID
            success = $markerReaderSelfTestComplete
            failureType = if ($markerReaderSelfTestComplete) {
                $null
            }
            else {
                "MarkerReaderSelfTestFailed"
            }
            errorType = $markerReaderSelfTestFailureType
            exitCode = if ($markerReaderSelfTestComplete) { 0 } else { 1 }
            durationMs = [Math]::Round(
                $markerReaderSelfTestStopwatch.Elapsed.TotalMilliseconds,
                3)
            timedOut = $false
            stdoutPolluted = $false
            stderrPolluted = $false
            unexpectedOutput = @()
            observedChildCount = 0
            observedChildren = @()
            transientChildCount = 0
            transientChildren = @()
            residualChildCount = 0
            residualChildren = @()
            childProcessObservationSampleCount = 0
            childProcessObservationDurationMs = 0.0
            residualChildEvidence = @()
            residualChildEvidenceStatus = "not-triggered"
            residualChildEvidenceErrorType = $null
            stdoutPath = $null
            stderrPath = $null
            evidence = @()
            slowEvidence = @()
            exitEvidence = @()
            timeoutEvidence = @()
            diagnosticCaptureDurationMs = 0.0
            slowThresholdExceeded = $false
            slowEvidenceStatus = "not-applicable"
            slowEvidenceErrorType = $null
            slowEvidenceTriggeredBeforeThreshold = $false
        }

        $script:markerReadContentionCount = 0
        $script:markerReadRetriesExhaustedCount = 0
        $script:markerReadErrorCount = 0
        $script:markerReadErrorType = $null
    }
}

foreach ($testProject in $testProjects) {
    $assemblyName = $testProject.BaseName
    $assemblyPath = Join-Path $testProject.DirectoryName (
        "bin/$Configuration/net10.0/$assemblyName.dll")
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Test assembly was not built: $assemblyPath"
    }

    Write-Host "Lifecycle probing $assemblyName ($resolvedIterations iteration(s))"
    for ($iteration = 1; $iteration -le $resolvedIterations; $iteration++) {
        $load = Invoke-IsolatedProcess `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @($probeAssembly, "--assembly", $assemblyPath)
        $phaseResults += New-ProcessPhaseResult -ProcessResult $load

        $assemblyInfo = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "assembly-info" `
            -Arguments @(
                $assemblyPath,
                "-assemblyInfo",
                "-automated",
                "sync"
            )
        $phaseResults += New-ProcessPhaseResult -ProcessResult $assemblyInfo

        $discovery = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "discovery" `
            -Arguments @(
                $assemblyPath,
                "-list",
                "full",
                "-automated",
                "sync",
                "-noLogo",
                "-noColor"
            )
        $phaseResults += New-ProcessPhaseResult -ProcessResult $discovery

        $markerPath = Join-Path $rawRoot (
            "$assemblyName/iteration-{0:D4}/execution.lifecycle" -f $iteration)
        $execution = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "execution" `
            -Arguments @(
                $assemblyPath,
                "-automated",
                "sync",
                "-noLogo",
                "-noColor",
                "-parallel",
                "none"
            ) `
            -Environment @{
                DOWNKYI_LIFECYCLE_MARKER = $markerPath
            } `
            -LifecycleMarkerPath $markerPath
        $phaseResults += New-ProcessPhaseResult -ProcessResult $execution

        $marker = Read-TeardownMarker -Path $markerPath
        $phaseResults += New-AssemblyCompletionPhaseResults `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Marker $marker `
            -Execution $execution `
            -ExitThresholdSeconds $ExitThresholdSeconds
    }
}

$runtime = (& dotnet --version).Trim()
$commitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$workingTreeDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
$report = New-AssemblyLifecycleReport `
    -PhaseResults $phaseResults `
    -Profile $Profile `
    -ResolvedIterations $resolvedIterations `
    -Runtime $runtime `
    -CommitSha $commitSha `
    -WorkingTreeDirty $workingTreeDirty `
    -TestAssemblyCount $testProjects.Count `
    -PhaseTimeoutSeconds $PhaseTimeoutSeconds `
    -SlowPhaseThresholdSeconds $SlowPhaseThresholdSeconds `
    -SlowEvidenceCaptureLeadMilliseconds $slowEvidenceCaptureLeadMilliseconds `
    -ResidualChildQuiescenceMilliseconds $residualChildQuiescenceMilliseconds `
    -ResidualChildPollMilliseconds $residualChildPollMilliseconds `
    -ForensicsSelfTestCaptureLeadValidated $forensicsSelfTestCaptureLeadValidated `
    -ReporterContractSelfTestPassed $reporterContractSelfTestPassed `
    -ExitThresholdSeconds $ExitThresholdSeconds `
    -DiagnosticsTool $script:diagnosticsTool `
    -OwnershipPassed $ownershipPassed `
    -OwnershipError $ownershipError `
    -MarkerReadContentionCount $script:markerReadContentionCount `
    -MarkerReadRetriesExhaustedCount $script:markerReadRetriesExhaustedCount `
    -MarkerReadErrorCount $script:markerReadErrorCount `
    -MarkerReadErrorType $script:markerReadErrorType `
    -MarkerReaderSelfTest $markerReaderSelfTest `
    -MarkerReaderSelfTestComplete $markerReaderSelfTestComplete `
    -ResidualChildSelfTest $residualChildSelfTest `
    -ResidualChildSelfTestComplete $residualChildSelfTestComplete
Write-AssemblyLifecycleReport `
    -Report $report `
    -RunRoot $runRoot `
    -OutputRoot $outputRoot `
    -RunId $runId

if (-not $report.successful) {
    throw "Assembly Lifecycle Stability Gate failed."
}
