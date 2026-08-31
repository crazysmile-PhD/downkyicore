function Invoke-IsolatedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$FileName,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [hashtable]$Environment = @{},
        [string]$LifecycleMarkerPath,
        [ValidateRange(1, 3600)]
        [int]$OperationTimeoutSeconds = $PhaseTimeoutSeconds,
        [double]$EvidenceThresholdSeconds = $SlowPhaseThresholdSeconds
    )

    $phaseDirectory = Join-Path $rawRoot (
        "$AssemblyName/iteration-{0:D4}" -f $Iteration)
    New-Item -ItemType Directory -Force -Path $phaseDirectory | Out-Null
    $stdoutPath = Join-Path $phaseDirectory "$Phase.stdout.txt"
    $stderrPath = Join-Path $phaseDirectory "$Phase.stderr.txt"

    $launchEnvironment = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    foreach ($entry in $Environment.GetEnumerator()) {
        $launchEnvironment[$entry.Key] = [string]$entry.Value
    }


    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(
        $FileName,
        [string[]]$Arguments,
        $repositoryRoot,
        $launchEnvironment,
        $true)
    $budget = [DownKyi.ProcessSupervision.TransitionBudget]::Start(
        [TimeSpan]::FromSeconds($OperationTimeoutSeconds),
        [TimeSpan]::FromSeconds($processCleanupGraceSeconds))
    $lease = $null
    $operationResult = $null
    $operationFailure = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $evidence = @()
    $slowEvidence = @()
    $exitEvidence = @()
    $timeoutEvidence = @()
    $residualChildEvidence = @()
    $diagnosticCaptureDurationMs = 0.0
    $slowThresholdExceeded = $false
    $slowEvidenceAttempted = $false
    $slowEvidenceCaptured = $false
    $slowEvidenceStatus = "not-triggered"
    $slowEvidenceErrorType = $null
    $slowEvidenceTriggeredBeforeThreshold = $false
    $residualChildEvidenceStatus = "not-triggered"
    $residualChildEvidenceErrorType = $null
    $exitEvidenceCaptured = $false
    $teardownObservedAt = $null
    $evidenceCaptureThresholdSeconds = [Math]::Max(
        0,
        $EvidenceThresholdSeconds - ($slowEvidenceCaptureLeadMilliseconds / 1000))
    try {
        $lease = [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
            $launchSpec,
            $budget).GetAwaiter().GetResult()

        $processId = $lease.TargetProcessId
        $waitTask = $lease.WaitAsync()
        $waitHandle = ([System.IAsyncResult]$waitTask).AsyncWaitHandle
        while (-not $waitHandle.WaitOne(25)) {
            if (-not $slowEvidenceAttempted -and
                $stopwatch.Elapsed.TotalSeconds -ge $evidenceCaptureThresholdSeconds) {
                $slowEvidenceTriggeredBeforeThreshold =
                    $stopwatch.Elapsed.TotalSeconds -lt $EvidenceThresholdSeconds
                $slowEvidenceAttempted = $true
                $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $diagnosticProcess = $null
                try {
                    $diagnosticProcess = Get-Process `
                        -Id $processId `
                        -ErrorAction SilentlyContinue
                    if ($null -eq $diagnosticProcess) {
                        $slowEvidenceStatus = "process-exited-before-capture"
                    }
                    else {
                        $evidencePath = Save-ProcessEvidence `
                            -Process $diagnosticProcess `
                            -AssemblyName $AssemblyName `
                            -Iteration $Iteration `
                            -Phase $Phase `
                            -Reason "slow-phase"
                        $evidence += $evidencePath
                        $slowEvidence += $evidencePath
                        $slowEvidenceCaptured = $true
                        $slowEvidenceStatus = "captured"
                    }
                }
                catch {
                    $slowEvidenceStatus = "capture-failed"
                    $slowEvidenceErrorType = $_.Exception.GetType().Name
                }
                finally {
                    if ($null -ne $diagnosticProcess) {
                        $diagnosticProcess.Dispose()
                    }
                    $captureStopwatch.Stop()
                    $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($LifecycleMarkerPath)) {
                $marker = Read-TeardownMarker -Path $LifecycleMarkerPath
                if ($null -eq $teardownObservedAt -and $null -ne $marker?.disposed) {
                    $teardownObservedAt = [DateTimeOffset]::UtcNow
                }

                if ($null -ne $teardownObservedAt -and
                    -not $exitEvidenceCaptured -and
                    ([DateTimeOffset]::UtcNow - $teardownObservedAt).TotalSeconds -ge
                        $ExitThresholdSeconds) {
                    $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                    $diagnosticProcess = $null
                    try {
                        $diagnosticProcess = Get-Process `
                            -Id $processId `
                            -ErrorAction SilentlyContinue
                        if ($null -ne $diagnosticProcess) {
                            $evidencePath = Save-ProcessEvidence `
                                -Process $diagnosticProcess `
                                -AssemblyName $AssemblyName `
                                -Iteration $Iteration `
                                -Phase $Phase `
                                -Reason "slow-exit-after-teardown"
                            $evidence += $evidencePath
                            $exitEvidence += $evidencePath
                        }
                    }
                    finally {
                        if ($null -ne $diagnosticProcess) {
                            $diagnosticProcess.Dispose()
                        }
                        $captureStopwatch.Stop()
                        $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                    }
                    $exitEvidenceCaptured = $true
                }
            }

        }

        $stopwatch.Stop()
        if ($stopwatch.Elapsed.TotalSeconds -ge $EvidenceThresholdSeconds) {
            $slowThresholdExceeded = $true
            if (-not $slowEvidenceAttempted) {
                $slowEvidenceStatus = "process-exited-before-capture"
            }
        }

        $outcome = $null
        $ownedFailure = $null
        try {
            $outcome = $waitTask.GetAwaiter().GetResult()
        }
        catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
            $ownedFailure = $_.Exception
        }
        finally {
            $waitTask.Dispose()
        }

        $processExitedAtUnixMs = if ($null -ne $outcome) {
            $outcome.TargetExitedAtUnixMilliseconds
        }
        else {
            $ownedFailure.Failure.TargetExitedAtUnixMilliseconds
        }
        $stdout = if ($null -ne $outcome) {
            $outcome.StandardOutput
        }
        else {
            $ownedFailure.Failure.StandardOutput
        }
        $stderr = if ($null -ne $outcome) {
            $outcome.StandardError
        }
        else {
            $ownedFailure.Failure.StandardError
        }
        [System.IO.File]::WriteAllText(
            $stdoutPath,
            $stdout,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            $stderrPath,
            $stderr,
            [System.Text.UTF8Encoding]::new($false))
        $ownedTreeQuiescent = if ($null -ne $outcome) {
            $outcome.TreeQuiescent
        }
        else {
            $ownedFailure.Failure.TreeQuiescent
        }
        $ownedFailureKind = if ($null -eq $ownedFailure) {
            $null
        }
        else {
            $ownedFailure.Failure.Kind.ToString()
        }
        $timedOut = $ownedFailureKind -in @(
            "OperationDeadlineExceeded",
            "StreamDrainDeadlineExceeded")
        $residualChildren = if (-not $ownedTreeQuiescent -and
            $ownedFailureKind -eq "OwnedTreeNotQuiescent") {
            @([pscustomobject]@{
                processId = $null
                parentProcessId = $null
                name = "owned-process-tree"
                createdAtUtc = $null
                containmentId = $ownedFailure.Failure.Ownership.ContainmentId
                containmentKind = $ownedFailure.Failure.Ownership.ContainmentKind.ToString()
            })
        }
        else {
            @()
        }
        if ($residualChildren.Count -gt 0) {
            $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $residualCapture = Save-OwnedTreeEvidence `
                    -Failure $ownedFailure.Failure `
                    -AssemblyName $AssemblyName `
                    -Iteration $Iteration `
                    -Phase $Phase
                $residualChildEvidence += $residualCapture.evidencePath
                $evidence += $residualCapture.evidencePath
                $residualChildEvidenceStatus = "captured"
                $residualChildEvidenceErrorType = $residualCapture.errorType
            }
            catch {
                $residualChildEvidenceStatus = "capture-failed"
                $residualChildEvidenceErrorType = $_.Exception.GetType().Name
            }
            finally {
                $captureStopwatch.Stop()
                $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
            }
        }
        $operationResult = [pscustomobject]@{
            assembly = $AssemblyName
            iteration = $Iteration
            phase = $Phase
            processId = $processId
            exitCode = if ($null -ne $outcome) { $outcome.ExitCode } else { 1 }
            durationMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            timedOut = $timedOut
            stdout = $stdout
            stderr = $stderr
            stdoutPath = [System.IO.Path]::GetRelativePath($runRoot, $stdoutPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            stderrPath = [System.IO.Path]::GetRelativePath($runRoot, $stderrPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            observedChildren = @()
            transientChildren = @()
            residualChildren = $residualChildren
            childProcessObservationSampleCount = 0
            childProcessObservationDurationMs = 0.0
            residualChildEvidence = @($residualChildEvidence)
            residualChildEvidenceStatus = $residualChildEvidenceStatus
            residualChildEvidenceErrorType = $residualChildEvidenceErrorType
            evidence = $evidence
            slowEvidence = $slowEvidence
            exitEvidence = $exitEvidence
            timeoutEvidence = $timeoutEvidence
            diagnosticCaptureDurationMs = [Math]::Round($diagnosticCaptureDurationMs, 3)
            slowThresholdExceeded = $slowThresholdExceeded
            slowEvidenceStatus = $slowEvidenceStatus
            slowEvidenceErrorType = $slowEvidenceErrorType
            slowEvidenceTriggeredBeforeThreshold =
                $slowEvidenceTriggeredBeforeThreshold
            ownedTreeQuiescent = $ownedTreeQuiescent
            ownedProcessFailureKind = $ownedFailureKind
            ownedProcessPrimaryFailure = if ($null -eq $ownedFailure) {
                $null
            }
            else {
                [pscustomobject]@{
                    type = $ownedFailure.InnerException.GetType().Name
                    message = $ownedFailure.InnerException.Message
                }
            }
            ownedProcessCleanupFailures = @(
                if ($null -ne $ownedFailure) {
                    $ownedFailure.CleanupFailures | ForEach-Object {
                        [pscustomobject]@{
                            type = $_.GetType().Name
                            message = $_.Message
                        }
                    }
                }
            )
            processOwnership = if ($null -ne $outcome) {
                $outcome.Ownership
            }
            else {
                $ownedFailure.Failure.Ownership
            }
            processExitedAtUnixMs = $processExitedAtUnixMs
            observedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
    }
    catch {
        $operationFailure = $_.Exception
    }

    $cleanupFailures = [Collections.Generic.List[Exception]]::new()
    try {
        if ($null -ne $lease) {
            [void]($lease.DisposeAsync().AsTask().GetAwaiter().GetResult())
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    if ($null -ne $operationFailure) {
        if ($cleanupFailures.Count -gt 0) {
            throw [AggregateException]::new(
                "Lifecycle phase and owned child-process cleanup both failed.",
                @($operationFailure) + $cleanupFailures.ToArray())
        }

        throw $operationFailure
    }
    if ($cleanupFailures.Count -gt 0) {
        throw [AggregateException]::new(
            "Lifecycle owned child-process cleanup failed.",
            $cleanupFailures.ToArray())
    }

    return $operationResult
}

function Assert-XunitSynchronousAutomatedReporting {
    param(
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $automatedIndexes = @(
        for ($index = 0; $index -lt $Arguments.Count; $index++) {
            if ($Arguments[$index] -ceq "-automated") {
                $index
            }
        }
    )
    $valid = $automatedIndexes.Count -eq 1 -and
        $automatedIndexes[0] + 1 -lt $Arguments.Count -and
        $Arguments[$automatedIndexes[0] + 1] -ceq "sync"
    if (-not $valid) {
        throw [System.InvalidOperationException]::new(
            "Lifecycle phase '$Phase' must use exactly one '-automated sync' reporter.")
    }
}

function Test-XunitReporterContractMutation {
    try {
        Assert-XunitSynchronousAutomatedReporting `
            -Phase "mutation-fixture" `
            -Arguments @("fixture.dll", "-automated", "async")
    }
    catch [System.InvalidOperationException] {
        return $true
    }

    return $false
}

function Invoke-XunitAutomatedPhase {
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [hashtable]$Environment = @{},
        [string]$LifecycleMarkerPath
    )

    Assert-XunitSynchronousAutomatedReporting -Phase $Phase -Arguments $Arguments
    return Invoke-IsolatedProcess `
        -AssemblyName $AssemblyName `
        -Iteration $Iteration `
        -Phase $Phase `
        -FileName "dotnet" `
        -Arguments $Arguments `
        -Environment $Environment `
        -LifecycleMarkerPath $LifecycleMarkerPath
}
