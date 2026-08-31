function Test-MarkerReaderSelfTestProof {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest
    )

    return $SelfTest.executed -eq $true -and
        $SelfTest.passed -eq $true -and
        $SelfTest.contentionObserved -eq $true -and
        $SelfTest.contentionCount -gt 0 -and
        $SelfTest.recoveredAfterLockRelease -eq $true -and
        $SelfTest.markerParsedAfterRecovery -eq $true -and
        $null -eq $SelfTest.errorType
}

function Test-JsonProtocol {
    param(
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Content
    )

    $lines = @($Content -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    try {
        if ($Phase -eq "execution") {
            if ($lines.Count -eq 0) {
                return $false
            }

            foreach ($line in $lines) {
                $null = $line | ConvertFrom-Json -ErrorAction Stop
            }
            return $true
        }

        if ($lines.Count -ne 1) {
            return $false
        }

        $payload = $lines[0] | ConvertFrom-Json -ErrorAction Stop
        if ($Phase -eq "load") {
            return $payload.Success -eq $true -and $payload.Unloaded -eq $true
        }

        if ($Phase -eq "discovery") {
            return $payload -is [System.Array]
        }

        return $null -ne $payload
    }
    catch [System.ArgumentException] {
        return $false
    }
    catch [System.Management.Automation.RuntimeException] {
        return $false
    }
}

function New-ProcessPhaseResult {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$ProcessResult
    )

    $forbiddenOutput = @(
        "Waiting 10 seconds for foreground threads to exit",
        "Unhandled exception",
        "Fatal error",
        "The active test run was aborted"
    )
    $unexpectedText = @(
        $forbiddenOutput |
            Where-Object {
                $ProcessResult.stdout.Contains($_, [StringComparison]::OrdinalIgnoreCase) -or
                $ProcessResult.stderr.Contains($_, [StringComparison]::OrdinalIgnoreCase)
            }
    )
    $protocolValid = Test-JsonProtocol `
        -Phase $ProcessResult.phase `
        -Content $ProcessResult.stdout
    $stderrClean = [string]::IsNullOrWhiteSpace($ProcessResult.stderr)
    $success = $ProcessResult.ownedProcessFormalGatePassed -eq $true -and
        $ProcessResult.exitCode -eq 0 -and
        $protocolValid -and
        $stderrClean -and
        $unexpectedText.Count -eq 0
    $failureType = if ($success) {
        $null
    }
    elseif (-not $ProcessResult.ownedProcessFormalGatePassed) {
        "OwnedProcessInvariantFailure"
    }
    elseif (-not $protocolValid -or -not $stderrClean -or $unexpectedText.Count -gt 0) {
        "OutputContractViolation"
    }
    else {
        "ProcessPhaseFailed"
    }
    return [pscustomobject]@{
        assembly = $ProcessResult.assembly
        iteration = $ProcessResult.iteration
        phase = $ProcessResult.phase
        processId = $ProcessResult.processId
        success = $success
        failureType = $failureType
        errorType = $null
        exitCode = $ProcessResult.exitCode
        durationMs = $ProcessResult.durationMs
        stdoutPolluted = -not $protocolValid -or $unexpectedText.Count -gt 0
        stderrPolluted = -not $stderrClean
        unexpectedOutput = $unexpectedText
        stdoutPath = $ProcessResult.stdoutPath
        stderrPath = $ProcessResult.stderrPath
        evidence = $ProcessResult.evidence
        slowEvidence = $ProcessResult.slowEvidence
        exitEvidence = $ProcessResult.exitEvidence
        diagnosticCaptureDurationMs = $ProcessResult.diagnosticCaptureDurationMs
        slowThresholdExceeded = $ProcessResult.slowThresholdExceeded
        slowEvidenceStatus = $ProcessResult.slowEvidenceStatus
        slowEvidenceErrorType = $ProcessResult.slowEvidenceErrorType
        slowEvidenceTriggeredBeforeThreshold =
            $ProcessResult.slowEvidenceTriggeredBeforeThreshold
        ownedProcessFormalGatePassed = $ProcessResult.ownedProcessFormalGatePassed
        ownedProcessInvariants = @($ProcessResult.ownedProcessInvariants)
        ownedProcessFailures = @($ProcessResult.ownedProcessFailures)
        ownedProcessProofPath = $ProcessResult.ownedProcessProofPath
    }
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)]
        [double[]]$Values,
        [Parameter(Mandatory)]
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(
        0,
        [Math]::Ceiling($Percentile * $sorted.Count) - 1)
    return [Math]::Round([double]$sorted[$index], 3)
}

function New-Statistics {
    param(
        [Parameter(Mandatory)]
        [object[]]$Results
    )

    return @(
        $Results |
            Group-Object assembly, phase |
            Sort-Object Name |
            ForEach-Object {
                $durations = [double[]]@($_.Group | ForEach-Object { $_.durationMs })
                $passed = @($_.Group | Where-Object success).Count
                $slow = @($_.Group | Where-Object slowThresholdExceeded)
                $slowCaptured = @(
                    $slow |
                        Where-Object slowEvidenceStatus -eq "captured"
                ).Count
                $diagnosticDurations = [double[]]@(
                    $_.Group |
                        ForEach-Object { $_.diagnosticCaptureDurationMs }
                )
                [pscustomobject]@{
                    assembly = $_.Group[0].assembly
                    phase = $_.Group[0].phase
                    runs = $_.Count
                    passed = $passed
                    successRate = [Math]::Round($passed / $_.Count, 6)
                    slowRuns = $slow.Count
                    slowEvidenceCaptured = $slowCaptured
                    slowEvidenceMissing = $slow.Count - $slowCaptured
                    diagnosticCaptureTotalMs = [Math]::Round(
                        [double]($diagnosticDurations | Measure-Object -Sum).Sum,
                        3)
                    diagnosticCaptureMaxMs = [Math]::Round(
                        [double]($diagnosticDurations | Measure-Object -Maximum).Maximum,
                        3)
                    p50Ms = Get-Percentile -Values $durations -Percentile 0.50
                    p95Ms = Get-Percentile -Values $durations -Percentile 0.95
                    p99Ms = Get-Percentile -Values $durations -Percentile 0.99
                    maxMs = [Math]::Round(
                        [double]($durations | Measure-Object -Maximum).Maximum,
                        3)
                }
            }
    )
}

function New-AssemblyCompletionPhaseResults {
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [AllowNull()]
        [object]$Marker,
        [Parameter(Mandatory)]
        [pscustomobject]$Execution,
        [Parameter(Mandatory)]
        [double]$ExitThresholdSeconds
    )

    $markerValid = $null -ne $marker -and
        $null -ne $marker.started -and
        $null -ne $marker.disposing -and
        $null -ne $marker.disposed -and
        $marker.started.processId -eq $marker.disposing.processId -and
        $marker.started.processId -eq $marker.disposed.processId
    $testRootRemoved = $false
    $teardownDuration = 0.0
    $exitDuration = [double]$execution.durationMs
    if ($markerValid) {
        $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            "downkyi-tests/$assemblyName/$($marker.started.processId)")
        $testRootRemoved = -not (Test-Path -LiteralPath $testRoot)
        $teardownDuration = [Math]::Max(
            0,
            [double]($marker.disposed.timestamp - $marker.disposing.timestamp))
        $exitDuration = [Math]::Max(
            0,
            [double]($execution.processExitedAtUnixMs - $marker.disposed.timestamp))
    }

    $teardownResult = [pscustomobject]@{
        assembly = $assemblyName
        iteration = $iteration
        phase = "assembly-teardown"
        success = $markerValid -and $testRootRemoved
        failureType = if ($markerValid -and $testRootRemoved) {
            $null
        }
        elseif (-not $markerValid) {
            "TeardownMarkerInvalid"
        }
        else {
            "TestDataCleanupFailed"
        }
        errorType = $null
        exitCode = if ($markerValid -and $testRootRemoved) { 0 } else { 1 }
        durationMs = $teardownDuration
        stdoutPolluted = $false
        stderrPolluted = $false
        unexpectedOutput = @()
        stdoutPath = $null
        stderrPath = $null
        evidence = @()
        slowEvidence = @()
        exitEvidence = @()
        diagnosticCaptureDurationMs = 0.0
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold = $false
        ownedProcessFormalGatePassed = $execution.ownedProcessFormalGatePassed
        ownedProcessInvariants = @($execution.ownedProcessInvariants)
        ownedProcessFailures = @($execution.ownedProcessFailures)
        ownedProcessProofPath = $execution.ownedProcessProofPath
    }
    $exitSucceeded = $execution.ownedProcessFormalGatePassed -eq $true
    $processExitResult = [pscustomobject]@{
        assembly = $assemblyName
        iteration = $iteration
        phase = "process-exit"
        success = $exitSucceeded
        failureType = if ($exitSucceeded) {
            $null
        }
        else {
            "OwnedProcessInvariantFailure"
        }
        errorType = $null
        exitCode = if ($exitSucceeded) { 0 } else { 1 }
        durationMs = [Math]::Round($exitDuration, 3)
        stdoutPolluted = $false
        stderrPolluted = $false
        unexpectedOutput = @()
        stdoutPath = $execution.stdoutPath
        stderrPath = $execution.stderrPath
        evidence = $execution.exitEvidence
        slowEvidence = @()
        exitEvidence = $execution.exitEvidence
        diagnosticCaptureDurationMs = 0.0
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold = $false
        ownedProcessFormalGatePassed = $execution.ownedProcessFormalGatePassed
        ownedProcessInvariants = @($execution.ownedProcessInvariants)
        ownedProcessFailures = @($execution.ownedProcessFailures)
        ownedProcessProofPath = $execution.ownedProcessProofPath
    }

    return @($teardownResult, $processExitResult)
}

function New-AssemblyLifecycleReport {
    param(
        [Parameter(Mandatory)]
        [object[]]$PhaseResults,
        [Parameter(Mandatory)]
        [string]$Profile,
        [Parameter(Mandatory)]
        [int]$ResolvedIterations,
        [Parameter(Mandatory)]
        [string]$Runtime,
        [Parameter(Mandatory)]
        [string]$CommitSha,
        [Parameter(Mandatory)]
        [bool]$WorkingTreeDirty,
        [Parameter(Mandatory)]
        [int]$TestAssemblyCount,
        [Parameter(Mandatory)]
        [int]$PhaseTimeoutSeconds,
        [Parameter(Mandatory)]
        [double]$SlowPhaseThresholdSeconds,
        [Parameter(Mandatory)]
        [int]$SlowEvidenceCaptureLeadMilliseconds,
        [Parameter(Mandatory)]
        [bool]$ForensicsSelfTestCaptureLeadValidated,
        [Parameter(Mandatory)]
        [bool]$ReporterContractSelfTestPassed,
        [Parameter(Mandatory)]
        [double]$ExitThresholdSeconds,
        [Parameter(Mandatory)]
        [bool]$OwnershipPassed,
        [AllowNull()]
        [object]$OwnershipError,
        [Parameter(Mandatory)]
        [int]$MarkerReadContentionCount,
        [Parameter(Mandatory)]
        [int]$MarkerReadRetriesExhaustedCount,
        [Parameter(Mandatory)]
        [int]$MarkerReadErrorCount,
        [AllowNull()]
        [object]$MarkerReadErrorType,
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$MarkerReaderSelfTest,
        [Parameter(Mandatory)]
        [bool]$MarkerReaderSelfTestComplete
    )

    $statistics = New-Statistics -Results $phaseResults
    $failedResults = @($phaseResults | Where-Object { -not $_.success })
    $slowResults = @($phaseResults | Where-Object slowThresholdExceeded)
    $slowEvidenceCapturedCount = @(
        $slowResults |
            Where-Object slowEvidenceStatus -eq "captured"
    ).Count
    $slowEvidenceMissingCount = $slowResults.Count - $slowEvidenceCapturedCount
    $markerReaderSelfTestContractPassed =
        -not $markerReaderSelfTest.required -or
        $markerReaderSelfTestComplete
    $diagnosticCaptureTotalMs = [Math]::Round(
        [double](
            $phaseResults |
                Measure-Object -Property diagnosticCaptureDurationMs -Sum
        ).Sum,
        3)
    return [ordered]@{
        schemaVersion = 2
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        profile = $Profile
        iterations = $resolvedIterations
        runtime = $runtime
        operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        commitSha = $commitSha
        workingTreeDirty = $workingTreeDirty
        testAssemblyCount = $TestAssemblyCount
        phaseTimeoutSeconds = $PhaseTimeoutSeconds
        slowPhaseThresholdSeconds = $SlowPhaseThresholdSeconds
        slowEvidenceCaptureLeadMilliseconds = $slowEvidenceCaptureLeadMilliseconds
        forensicsSelfTestCaptureLeadValidated =
            $forensicsSelfTestCaptureLeadValidated
        reporterContractSelfTestPassed = $reporterContractSelfTestPassed
        exitThresholdSeconds = $ExitThresholdSeconds
        ownershipAuditPassed = $ownershipPassed
        ownershipAuditErrorType = $ownershipError
        successful = $ownershipPassed -and
            $failedResults.Count -eq 0 -and
            $markerReaderSelfTestContractPassed
        failedPhaseCount = $failedResults.Count
        slowPhaseCount = $slowResults.Count
        slowEvidenceCapturedCount = $slowEvidenceCapturedCount
        slowEvidenceMissingCount = $slowEvidenceMissingCount
        diagnosticCaptureTotalMs = $diagnosticCaptureTotalMs
        markerReadContentionCount = $MarkerReadContentionCount
        markerReadRetriesExhaustedCount = $MarkerReadRetriesExhaustedCount
        markerReadErrorCount = $MarkerReadErrorCount
        markerReadErrorType = $MarkerReadErrorType
        markerReaderSelfTestPassed = if ($markerReaderSelfTest.executed) {
            $markerReaderSelfTestComplete
        }
        else {
            $null
        }
        markerReaderSelfTest = $markerReaderSelfTest
        statistics = $statistics
        results = $phaseResults
    }
}

function New-MarkerReaderSelfTestState {
    param(
        [Parameter(Mandatory)]
        [bool]$Required
    )

    return [ordered]@{
        required = $Required
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
}

function New-ForensicsSelfTestPhaseResult {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$SelfTest,
        [Parameter(Mandatory)]
        [pscustomobject]$SelfTestPhase,
        [Parameter(Mandatory)]
        [object[]]$EvidenceReports
    )

    $diagnosticEvidenceCaptured = @(
        $evidenceReports | Where-Object {
            $_.diagnosticOnly -eq $true -and
            $_.captureMode -eq "owned-diagnostic-collector" -and
            $_.managedStack.captured -eq $true
        }
    ).Count -gt 0
    $forensicsValid = $selfTestPhase.success
    $phaseResult = [pscustomobject]@{
        assembly = "Gate.Forensics"
        iteration = 1
        phase = "forensics-self-test"
        processId = $selfTest.processId
        success = $forensicsValid
        failureType = if ($forensicsValid) { $null } else { "ForensicsSelfTestFailed" }
        errorType = $selfTestPhase.errorType
        exitCode = if ($forensicsValid) { 0 } else { 1 }
        durationMs = $selfTest.durationMs
        stdoutPolluted = $selfTestPhase.stdoutPolluted
        stderrPolluted = $selfTestPhase.stderrPolluted
        unexpectedOutput = $selfTestPhase.unexpectedOutput
        stdoutPath = $selfTest.stdoutPath
        stderrPath = $selfTest.stderrPath
        evidence = $selfTest.evidence
        slowEvidence = $selfTest.slowEvidence
        exitEvidence = $selfTest.exitEvidence
        diagnosticCaptureDurationMs = $selfTest.diagnosticCaptureDurationMs
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold =
            $selfTest.slowEvidenceTriggeredBeforeThreshold
        ownedProcessFormalGatePassed = $selfTestPhase.ownedProcessFormalGatePassed
        ownedProcessInvariants = @($selfTestPhase.ownedProcessInvariants)
        ownedProcessFailures = @($selfTestPhase.ownedProcessFailures)
        ownedProcessProofPath = $selfTestPhase.ownedProcessProofPath
        diagnosticEvidenceCaptured = $diagnosticEvidenceCaptured
    }

    return [pscustomobject]@{
        phaseResult = $phaseResult
        captureLeadValidated =
            $diagnosticEvidenceCaptured -and
            $selfTest.slowEvidenceTriggeredBeforeThreshold
    }
}

function Set-MarkerReaderSelfTestObservations {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest,
        [AllowNull()]
        [object]$LockedMarker,
        [AllowNull()]
        [object]$UnlockedMarker,
        [Parameter(Mandatory)]
        [int]$ContentionCount
    )

    $SelfTest.contentionCount = $ContentionCount
    $SelfTest.contentionObserved = $ContentionCount -gt 0
    $SelfTest.recoveredAfterLockRelease = $null -ne $UnlockedMarker
    $SelfTest.markerParsedAfterRecovery =
        $null -ne $UnlockedMarker -and
        $null -ne $UnlockedMarker.started -and
        $null -ne $UnlockedMarker.disposing -and
        $null -ne $UnlockedMarker.disposed
    $SelfTest.passed =
        $null -eq $LockedMarker -and
        $SelfTest.contentionObserved -and
        $SelfTest.recoveredAfterLockRelease -and
        $SelfTest.markerParsedAfterRecovery
}

function Complete-MarkerReaderSelfTestClassification {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest
    )

    $markerReaderSelfTest = $SelfTest
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

    return [pscustomobject]@{
        complete = $markerReaderSelfTestComplete
        errorType = $markerReaderSelfTestFailureType
    }
}

function New-MarkerReaderSelfTestPhaseResult {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest,
        [Parameter(Mandatory)]
        [bool]$Complete,
        [AllowNull()]
        [object]$ErrorType,
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch
    )

    $markerReaderSelfTest = $SelfTest
    $markerReaderSelfTestComplete = $Complete
    $markerReaderSelfTestFailureType = $ErrorType
    $markerReaderSelfTestStopwatch = $Stopwatch
    return [pscustomobject]@{
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
        stdoutPolluted = $false
        stderrPolluted = $false
        unexpectedOutput = @()
        stdoutPath = $null
        stderrPath = $null
        evidence = @()
        slowEvidence = @()
        exitEvidence = @()
        diagnosticCaptureDurationMs = 0.0
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold = $false
    }
}
