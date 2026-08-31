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
    $containmentRequirement = [Enum]::Parse(
        [DownKyi.ProcessSupervision.ProcessContainmentRequirement],
        $processContainmentRequirement)
    $lease = $null
    $ownedOutcome = $null
    $operationResult = $null
    $operationFailure = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $evidence = @()
    $slowEvidence = @()
    $exitEvidence = @()
    $diagnosticCaptureDurationMs = 0.0
    $slowThresholdExceeded = $false
    $slowEvidenceAttempted = $false
    $slowEvidenceStatus = "not-triggered"
    $slowEvidenceErrorType = $null
    $slowEvidenceTriggeredBeforeThreshold = $false
    $exitEvidenceStatus = "not-triggered"
    $exitEvidenceErrorType = $null
    $exitEvidenceCaptured = $false
    $teardownObservedAt = $null
    $evidenceCaptureThresholdSeconds = [Math]::Max(
        0,
        $EvidenceThresholdSeconds - ($slowEvidenceCaptureLeadMilliseconds / 1000))
    try {
        try {
            $lease = [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
                $launchSpec,
                $budget,
                $containmentRequirement).GetAwaiter().GetResult()

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
                    try {
                        $evidencePath = Save-ProcessEvidence `
                            -TargetProcessId $processId `
                            -AssemblyName $AssemblyName `
                            -Iteration $Iteration `
                            -Phase $Phase `
                            -Reason "slow-phase"
                        $evidence += $evidencePath
                        $slowEvidence += $evidencePath
                        $slowEvidenceStatus = "captured"
                    }
                    catch {
                        $slowEvidenceStatus = "capture-failed"
                        $slowEvidenceErrorType = $_.Exception.GetType().Name
                    }
                    finally {
                        $captureStopwatch.Stop()
                        $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($LifecycleMarkerPath)) {
                    $marker = Read-TeardownMarker -Path $LifecycleMarkerPath
                    if ($null -eq $teardownObservedAt -and
                        $null -ne $marker -and
                        $null -ne $marker.disposed) {
                        $teardownObservedAt = [DateTimeOffset]::UtcNow
                    }

                    if ($null -ne $teardownObservedAt -and
                        -not $exitEvidenceCaptured -and
                        ([DateTimeOffset]::UtcNow - $teardownObservedAt).TotalSeconds -ge
                            $ExitThresholdSeconds) {
                        $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                        try {
                            $evidencePath = Save-ProcessEvidence `
                                -TargetProcessId $processId `
                                -AssemblyName $AssemblyName `
                                -Iteration $Iteration `
                                -Phase $Phase `
                                -Reason "slow-exit-after-teardown"
                            $evidence += $evidencePath
                            $exitEvidence += $evidencePath
                            $exitEvidenceStatus = "captured"
                        }
                        catch {
                            $exitEvidenceStatus = "capture-failed"
                            $exitEvidenceErrorType = $_.Exception.GetType().Name
                        }
                        finally {
                            $captureStopwatch.Stop()
                            $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                        }
                        $exitEvidenceCaptured = $true
                    }
                }
            }

            try {
                $ownedOutcome = $waitTask.GetAwaiter().GetResult()
            }
            catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
                $ownedOutcome = $_.Exception.Outcome
            }
            finally {
                $waitTask.Dispose()
            }
        }
        catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
            $ownedOutcome = $_.Exception.Outcome
        }

        $stopwatch.Stop()
        if ($stopwatch.Elapsed.TotalSeconds -ge $EvidenceThresholdSeconds) {
            $slowThresholdExceeded = $true
            if (-not $slowEvidenceAttempted) {
                $slowEvidenceStatus = "process-exited-before-capture"
            }
        }

        if ($null -eq $ownedOutcome) {
            throw "Owned process supervision returned no typed outcome for $AssemblyName/$Phase."
        }

        $stdout = $ownedOutcome.StandardOutput
        $stderr = $ownedOutcome.StandardError
        [System.IO.File]::WriteAllText(
            $stdoutPath,
            $stdout,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            $stderrPath,
            $stderr,
            [System.Text.UTF8Encoding]::new($false))
        $ownedInvariants = @($ownedOutcome.Invariants)
        $ownedProofPath = Join-Path $phaseDirectory "$Phase.owned-process.json"
        $ownedProof = New-OwnedProcessProof -Outcome $ownedOutcome
        $ownedInvariantEvidence = @($ownedProof.invariants)
        $ownedFailureEvidence = @($ownedProof.failures)
        $ownedFactEvidence = @($ownedProof.facts)
        [System.IO.File]::WriteAllText(
            $ownedProofPath,
            ($ownedProof | ConvertTo-Json -Depth 8),
            [System.Text.UTF8Encoding]::new($false))
        $operationResult = [pscustomobject]@{
            assembly = $AssemblyName
            iteration = $Iteration
            phase = $Phase
            processId = $ownedOutcome.TargetProcessId
            exitCode = if ($ownedOutcome.FormalGatePassed -and
                $null -ne $ownedOutcome.ExitCode) {
                $ownedOutcome.ExitCode
            }
            else {
                1
            }
            durationMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            stdout = $stdout
            stderr = $stderr
            stdoutPath = [System.IO.Path]::GetRelativePath($runRoot, $stdoutPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            stderrPath = [System.IO.Path]::GetRelativePath($runRoot, $stderrPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            evidence = $evidence
            slowEvidence = $slowEvidence
            exitEvidence = $exitEvidence
            exitEvidenceStatus = $exitEvidenceStatus
            exitEvidenceErrorType = $exitEvidenceErrorType
            diagnosticCaptureDurationMs = [Math]::Round($diagnosticCaptureDurationMs, 3)
            slowThresholdExceeded = $slowThresholdExceeded
            slowEvidenceStatus = $slowEvidenceStatus
            slowEvidenceErrorType = $slowEvidenceErrorType
            slowEvidenceTriggeredBeforeThreshold =
                $slowEvidenceTriggeredBeforeThreshold
            ownedProcessFormalGatePassed = $ownedOutcome.FormalGatePassed
            ownedProcessInvariants = $ownedInvariantEvidence
            ownedProcessFailures = $ownedFailureEvidence
            ownedProcessFacts = $ownedFactEvidence
            ownedProcessProofPath = [System.IO.Path]::GetRelativePath(
                $runRoot,
                $ownedProofPath).Replace(
                    [System.IO.Path]::DirectorySeparatorChar,
                    '/')
            processOwnership = $ownedOutcome.Ownership
            processExitedAtUnixMs = $ownedOutcome.TargetExitObservedAtUnixMilliseconds
            observedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
    }
    catch {
        $operationFailure = $_.Exception
    }
    finally {
        if ($null -ne $lease) {
            try {
                [void]($lease.DisposeAsync().AsTask().GetAwaiter().GetResult())
            }
            catch {
                if ($null -eq $operationFailure) {
                    $operationFailure = $_.Exception
                }
            }
        }
    }

    if ($null -ne $operationFailure) {
        throw $operationFailure
    }
    if (-not $operationResult.ownedProcessFormalGatePassed) {
        throw [System.InvalidOperationException]::new(
            "Owned process formal gate failed for $AssemblyName/$Phase. " +
            "Typed proof: $($operationResult.ownedProcessProofPath)")
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
