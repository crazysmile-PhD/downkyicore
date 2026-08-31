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
        [double]$EvidenceThresholdSeconds = $SlowPhaseThresholdSeconds
    )

    $phaseDirectory = Join-Path $rawRoot (
        "$AssemblyName/iteration-{0:D4}" -f $Iteration)
    New-Item -ItemType Directory -Force -Path $phaseDirectory | Out-Null
    $stdoutPath = Join-Path $phaseDirectory "$Phase.stdout.txt"
    $stderrPath = Join-Path $phaseDirectory "$Phase.stderr.txt"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
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
        if (-not $process.Start()) {
            throw "Process did not start for $AssemblyName/$Phase."
        }

        $processId = $process.Id
        $processStartedAt = [DateTimeOffset]$process.StartTime.ToUniversalTime()
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(25)) {
            if (-not $slowEvidenceAttempted -and
                $stopwatch.Elapsed.TotalSeconds -ge $evidenceCaptureThresholdSeconds) {
                $slowEvidenceTriggeredBeforeThreshold =
                    $stopwatch.Elapsed.TotalSeconds -lt $EvidenceThresholdSeconds
                $slowEvidenceAttempted = $true
                $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $evidencePath = Save-ProcessEvidence `
                        -Process $process `
                        -AssemblyName $AssemblyName `
                        -Iteration $Iteration `
                        -Phase $Phase `
                        -Reason "slow-phase"
                    $evidence += $evidencePath
                    $slowEvidence += $evidencePath
                    $slowEvidenceCaptured = $true
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
                if ($null -eq $teardownObservedAt -and $null -ne $marker?.disposed) {
                    $teardownObservedAt = [DateTimeOffset]::UtcNow
                }

                if ($null -ne $teardownObservedAt -and
                    -not $exitEvidenceCaptured -and
                    ([DateTimeOffset]::UtcNow - $teardownObservedAt).TotalSeconds -ge
                        $ExitThresholdSeconds) {
                    $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $evidencePath = Save-ProcessEvidence `
                            -Process $process `
                            -AssemblyName $AssemblyName `
                            -Iteration $Iteration `
                            -Phase $Phase `
                            -Reason "slow-exit-after-teardown"
                        $evidence += $evidencePath
                        $exitEvidence += $evidencePath
                    }
                    finally {
                        $captureStopwatch.Stop()
                        $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                    }
                    $exitEvidenceCaptured = $true
                }
            }

            if ($stopwatch.Elapsed.TotalSeconds -ge $PhaseTimeoutSeconds) {
                $timedOut = $true
                $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $evidencePath = Save-ProcessEvidence `
                        -Process $process `
                        -AssemblyName $AssemblyName `
                        -Iteration $Iteration `
                        -Phase $Phase `
                        -Reason "timeout"
                    $evidence += $evidencePath
                    $timeoutEvidence += $evidencePath
                }
                finally {
                    $captureStopwatch.Stop()
                    $diagnosticCaptureDurationMs += $captureStopwatch.Elapsed.TotalMilliseconds
                }
                $process.Kill($true)
                $process.WaitForExit()
                break
            }
        }

        $stopwatch.Stop()
        if ($stopwatch.Elapsed.TotalSeconds -ge $EvidenceThresholdSeconds) {
            $slowThresholdExceeded = $true
            if (-not $slowEvidenceAttempted) {
                $slowEvidenceStatus = "process-exited-before-capture"
            }
        }

        $processExitedAtUnixMs = ([DateTimeOffset]$process.ExitTime.ToUniversalTime()).
            ToUnixTimeMilliseconds()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText(
            $stdoutPath,
            $stdout,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            $stderrPath,
            $stderr,
            [System.Text.UTF8Encoding]::new($false))
        $childProcessObservation = Wait-ResidualProcessTree `
                -RootProcessId $processId `
                -NotBeforeUtc $processStartedAt `
                -QuiescenceMilliseconds $residualChildQuiescenceMilliseconds `
                -PollMilliseconds $residualChildPollMilliseconds
        $observedChildren = @($childProcessObservation.observedChildren)
        $transientChildren = @($childProcessObservation.transientChildren)
        $residualChildren = @($childProcessObservation.residualChildren)
        if ($residualChildren.Count -gt 0) {
            $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $residualCapture = Save-ResidualChildEvidence `
                    -Children $residualChildren `
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
        return [pscustomobject]@{
            assembly = $AssemblyName
            iteration = $Iteration
            phase = $Phase
            processId = $processId
            exitCode = $process.ExitCode
            durationMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            timedOut = $timedOut
            stdout = $stdout
            stderr = $stderr
            stdoutPath = [System.IO.Path]::GetRelativePath($runRoot, $stdoutPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            stderrPath = [System.IO.Path]::GetRelativePath($runRoot, $stderrPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            observedChildren = $observedChildren
            transientChildren = $transientChildren
            residualChildren = $residualChildren
            childProcessObservationSampleCount =
                $childProcessObservation.sampleCount
            childProcessObservationDurationMs =
                $childProcessObservation.elapsedMilliseconds
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
            processExitedAtUnixMs = $processExitedAtUnixMs
            observedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
    }
    finally {
        $process.Dispose()
    }
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
