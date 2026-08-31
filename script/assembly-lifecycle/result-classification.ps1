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
    $slowEvidenceComplete = -not $ProcessResult.slowThresholdExceeded -or
        $ProcessResult.slowEvidenceStatus -eq "captured"
    $success = $ProcessResult.exitCode -eq 0 -and
        -not $ProcessResult.timedOut -and
        $ProcessResult.residualChildren.Count -eq 0 -and
        $protocolValid -and
        $stderrClean -and
        $slowEvidenceComplete -and
        $unexpectedText.Count -eq 0
    $failureType = if ($success) {
        $null
    }
    elseif ($ProcessResult.timedOut) {
        "Timeout"
    }
    elseif (-not $slowEvidenceComplete) {
        "SlowEvidenceMissing"
    }
    elseif ($ProcessResult.residualChildren.Count -gt 0) {
        "ResidualChildProcess"
    }
    elseif (-not $protocolValid -or -not $stderrClean -or $unexpectedText.Count -gt 0) {
        "OutputContractViolation"
    }
    else {
        "ProcessPhaseFailed"
    }
    $errorType = if ($failureType -eq "SlowEvidenceMissing") {
        $ProcessResult.slowEvidenceErrorType
    }
    elseif ($failureType -eq "ResidualChildProcess") {
        $ProcessResult.residualChildEvidenceErrorType
    }
    else {
        $null
    }
    return [pscustomobject]@{
        assembly = $ProcessResult.assembly
        iteration = $ProcessResult.iteration
        phase = $ProcessResult.phase
        processId = $ProcessResult.processId
        success = $success
        failureType = $failureType
        errorType = $errorType
        exitCode = $ProcessResult.exitCode
        durationMs = $ProcessResult.durationMs
        timedOut = $ProcessResult.timedOut
        stdoutPolluted = -not $protocolValid -or $unexpectedText.Count -gt 0
        stderrPolluted = -not $stderrClean
        unexpectedOutput = $unexpectedText
        observedChildCount = $ProcessResult.observedChildren.Count
        observedChildren = @($ProcessResult.observedChildren)
        transientChildCount = $ProcessResult.transientChildren.Count
        transientChildren = @($ProcessResult.transientChildren)
        residualChildCount = $ProcessResult.residualChildren.Count
        residualChildren = @($ProcessResult.residualChildren)
        childProcessObservationSampleCount =
            $ProcessResult.childProcessObservationSampleCount
        childProcessObservationDurationMs =
            $ProcessResult.childProcessObservationDurationMs
        residualChildEvidence = @($ProcessResult.residualChildEvidence)
        residualChildEvidenceStatus = $ProcessResult.residualChildEvidenceStatus
        residualChildEvidenceErrorType = $ProcessResult.residualChildEvidenceErrorType
        stdoutPath = $ProcessResult.stdoutPath
        stderrPath = $ProcessResult.stderrPath
        evidence = $ProcessResult.evidence
        slowEvidence = $ProcessResult.slowEvidence
        exitEvidence = $ProcessResult.exitEvidence
        timeoutEvidence = $ProcessResult.timeoutEvidence
        diagnosticCaptureDurationMs = $ProcessResult.diagnosticCaptureDurationMs
        slowThresholdExceeded = $ProcessResult.slowThresholdExceeded
        slowEvidenceStatus = $ProcessResult.slowEvidenceStatus
        slowEvidenceErrorType = $ProcessResult.slowEvidenceErrorType
        slowEvidenceTriggeredBeforeThreshold =
            $ProcessResult.slowEvidenceTriggeredBeforeThreshold
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
    $exitSucceeded = $execution.exitCode -eq 0 -and
        -not $execution.timedOut -and
        $execution.residualChildren.Count -eq 0 -and
        $exitDuration -le ($ExitThresholdSeconds * 1000)
    $processExitResult = [pscustomobject]@{
        assembly = $assemblyName
        iteration = $iteration
        phase = "process-exit"
        success = $exitSucceeded
        failureType = if ($exitSucceeded) { $null } else { "ProcessExitFailed" }
        errorType = $null
        exitCode = if ($exitSucceeded) { 0 } else { 1 }
        durationMs = [Math]::Round($exitDuration, 3)
        timedOut = $execution.timedOut
        stdoutPolluted = $false
        stderrPolluted = $false
        unexpectedOutput = @()
        observedChildCount = $execution.observedChildren.Count
        observedChildren = @($execution.observedChildren)
        transientChildCount = $execution.transientChildren.Count
        transientChildren = @($execution.transientChildren)
        residualChildCount = $execution.residualChildren.Count
        residualChildren = @($execution.residualChildren)
        childProcessObservationSampleCount =
            $execution.childProcessObservationSampleCount
        childProcessObservationDurationMs =
            $execution.childProcessObservationDurationMs
        residualChildEvidence = @($execution.residualChildEvidence)
        residualChildEvidenceStatus = $execution.residualChildEvidenceStatus
        residualChildEvidenceErrorType = $execution.residualChildEvidenceErrorType
        stdoutPath = $execution.stdoutPath
        stderrPath = $execution.stderrPath
        evidence = $execution.exitEvidence
        slowEvidence = @()
        exitEvidence = $execution.exitEvidence
        timeoutEvidence = $execution.timeoutEvidence
        diagnosticCaptureDurationMs = 0.0
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold = $false
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
        [int]$ResidualChildQuiescenceMilliseconds,
        [Parameter(Mandatory)]
        [int]$ResidualChildPollMilliseconds,
        [Parameter(Mandatory)]
        [bool]$ForensicsSelfTestCaptureLeadValidated,
        [Parameter(Mandatory)]
        [bool]$ReporterContractSelfTestPassed,
        [Parameter(Mandatory)]
        [double]$ExitThresholdSeconds,
        [AllowNull()]
        [object]$DiagnosticsTool,
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
        [bool]$MarkerReaderSelfTestComplete,
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$ResidualChildSelfTest,
        [Parameter(Mandatory)]
        [bool]$ResidualChildSelfTestComplete
    )

    $statistics = New-Statistics -Results $phaseResults
    $failedResults = @($phaseResults | Where-Object { -not $_.success })
    $slowResults = @($phaseResults | Where-Object slowThresholdExceeded)
    $slowEvidenceCapturedCount = @(
        $slowResults |
            Where-Object slowEvidenceStatus -eq "captured"
    ).Count
    $slowEvidenceMissingCount = $slowResults.Count - $slowEvidenceCapturedCount
    $residualChildResults = @(
        $phaseResults | Where-Object residualChildCount -gt 0
    )
    $transientChildResults = @(
        $phaseResults | Where-Object transientChildCount -gt 0
    )
    $transientChildObservedCount = [int](
        $transientChildResults |
            Measure-Object -Property transientChildCount -Sum
    ).Sum
    $residualChildObservedCount = [int](
        $residualChildResults |
            Measure-Object -Property residualChildCount -Sum
    ).Sum
    $residualChildEvidenceCapturedCount = @(
        $residualChildResults |
            Where-Object residualChildEvidenceStatus -eq "captured"
    ).Count
    $residualChildEvidenceMissingCount =
        $residualChildResults.Count - $residualChildEvidenceCapturedCount
    $markerReaderSelfTestContractPassed =
        -not $markerReaderSelfTest.required -or
        $markerReaderSelfTestComplete
    $residualChildSelfTestContractPassed =
        -not $residualChildSelfTest.required -or
        $residualChildSelfTestComplete
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
        residualChildQuiescenceMilliseconds =
            $residualChildQuiescenceMilliseconds
        residualChildPollMilliseconds = $residualChildPollMilliseconds
        forensicsSelfTestCaptureLeadValidated =
            $forensicsSelfTestCaptureLeadValidated
        reporterContractSelfTestPassed = $reporterContractSelfTestPassed
        exitThresholdSeconds = $ExitThresholdSeconds
        diagnosticsTool = if ($null -eq $DiagnosticsTool) {
            "unavailable"
        }
        else {
            [System.IO.Path]::GetFileName($DiagnosticsTool)
        }
        ownershipAuditPassed = $ownershipPassed
        ownershipAuditErrorType = $ownershipError
        successful = $ownershipPassed -and
            $failedResults.Count -eq 0 -and
            $markerReaderSelfTestContractPassed -and
            $residualChildSelfTestContractPassed
        failedPhaseCount = $failedResults.Count
        slowPhaseCount = $slowResults.Count
        slowEvidenceCapturedCount = $slowEvidenceCapturedCount
        slowEvidenceMissingCount = $slowEvidenceMissingCount
        residualChildPhaseCount = $residualChildResults.Count
        residualChildObservedCount = $residualChildObservedCount
        transientChildPhaseCount = $transientChildResults.Count
        transientChildObservedCount = $transientChildObservedCount
        residualChildEvidenceCapturedCount = $residualChildEvidenceCapturedCount
        residualChildEvidenceMissingCount = $residualChildEvidenceMissingCount
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
        residualChildSelfTestPassed = if ($residualChildSelfTest.executed) {
            $residualChildSelfTestComplete
        }
        else {
            $null
        }
        residualChildSelfTest = $residualChildSelfTest
        statistics = $statistics
        results = $phaseResults
    }
}

function New-ResidualChildSelfTestState {
    param(
        [Parameter(Mandatory)]
        [bool]$Required
    )

    return [ordered]@{
        required = $Required
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

    $forensicsValid = $selfTestPhase.success -and
        $evidenceReports.Count -gt 0 -and
        @($evidenceReports | Where-Object { $_.managedStack.captured -eq $true }).Count -gt 0 -and
        $selfTest.slowEvidenceTriggeredBeforeThreshold
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

    return [pscustomobject]@{
        phaseResult = $phaseResult
        captureLeadValidated = $selfTest.slowEvidenceTriggeredBeforeThreshold
    }
}

function Set-ResidualChildSelfTestPersistentObservations {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest,
        [Parameter(Mandatory)]
        [pscustomobject]$ResidualProbe,
        [Parameter(Mandatory)]
        [pscustomobject]$ResidualProbePhase,
        [Parameter(Mandatory)]
        [string]$RunRoot
    )

    $residualChildSelfTest = $SelfTest
    $runRoot = $RunRoot
    $residualPayload = $residualProbe.stdout | ConvertFrom-Json -ErrorAction Stop
    $expectedChildProcessId = [int]$residualPayload.ChildProcessId
    $observedResidualChildren = @($residualProbe.residualChildren)
    $residualChildSelfTest.observedChildCount =
        $observedResidualChildren.Count
    $residualChildSelfTest.childObserved =
        $expectedChildProcessId -gt 0 -and
        $observedResidualChildren.Count -eq 1 -and
        $residualProbe.ownedProcessFailureKind -eq "OwnedTreeNotQuiescent"
    $residualChildSelfTest.identityCaptured =
        $null -ne $residualProbe.processOwnership -and
        $residualProbe.processOwnership.OwnershipEstablished -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$residualProbe.processOwnership.ContainmentId) -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$residualProbe.processOwnership.MembershipId)
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
}

function Set-ResidualChildSelfTestTransientObservations {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest,
        [Parameter(Mandatory)]
        [pscustomobject]$TransientProbe,
        [Parameter(Mandatory)]
        [pscustomobject]$TransientProbePhase,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $residualChildSelfTest = $SelfTest
    $repositoryRoot = $RepositoryRoot
    $transientPayload = $transientProbe.stdout |
        ConvertFrom-Json -ErrorAction Stop
    $expectedTransientProcessId = [int]$transientPayload.ChildProcessId
    $residualChildSelfTest.transientChildObserved =
        $expectedTransientProcessId -gt 0 -and
        $transientProbe.ownedTreeQuiescent
    $residualChildSelfTest.transientChildDrained =
        $transientProbe.ownedTreeQuiescent -and
        @($transientProbe.residualChildren).Count -eq 0
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

function Complete-ResidualChildSelfTestClassification {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest
    )

    $residualChildSelfTest = $SelfTest
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
    return $residualChildSelfTestComplete
}

function New-ResidualChildSelfTestPhaseResult {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$SelfTest,
        [Parameter(Mandatory)]
        [bool]$Complete,
        [AllowNull()]
        [pscustomobject]$ResidualProbe,
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch
    )

    $residualChildSelfTest = $SelfTest
    $residualChildSelfTestComplete = $Complete
    $residualChildSelfTestStopwatch = $Stopwatch
    return [pscustomobject]@{
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
}
