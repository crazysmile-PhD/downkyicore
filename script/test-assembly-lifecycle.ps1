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
$solutionPath = Join-Path $repositoryRoot "DownKyi.sln"
$probeProject = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj"
$probeAssembly = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/bin/$Configuration/net10.0/DownKyi.AssemblyLifecycleProbe.dll"
$profileIterations = @{
    Local = 1
    PR = 3
    Main = 50
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

New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

if ($markerReaderSelfTestRequired -and -not $ValidateForensics) {
    throw "Formal Windows lifecycle profiles require -ValidateForensics."
}

function Resolve-DiagnosticsTool {
    if (-not [string]::IsNullOrWhiteSpace($DiagnosticsToolPath)) {
        $resolved = [System.IO.Path]::GetFullPath($DiagnosticsToolPath, (Get-Location).Path)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }

        throw "Diagnostics tool was not found: $resolved"
    }

    $localNames = if ($IsWindows) {
        @("dotnet-stack.exe", "dotnet-stack")
    }
    else {
        @("dotnet-stack")
    }
    foreach ($name in $localNames) {
        $candidate = Join-Path $repositoryRoot ".tools/$name"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $command = Get-Command "dotnet-stack" -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Protect-ProcessDiagnosticText {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $protected = $Value
    $pathAliases = @(
        [pscustomobject]@{ path = $repositoryRoot; alias = "<repository>" }
        [pscustomobject]@{
            path = [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::UserProfile)
            alias = "<user-profile>"
        }
        [pscustomobject]@{
            path = [System.IO.Path]::GetTempPath().TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            alias = "<temp>"
        }
    )
    foreach ($pathAlias in $pathAliases) {
        if (-not [string]::IsNullOrWhiteSpace($pathAlias.path)) {
            $protected = $protected.Replace(
                $pathAlias.path,
                $pathAlias.alias,
                [StringComparison]::OrdinalIgnoreCase)
        }
    }

    $protected = $protected -replace '(?i)https?://\S+', '<url>'
    $protected = $protected -replace (
        '(?i)(SESSDATA|bili_jct|DedeUserID|cookie|token|secret)' +
        '\s*[:=]\s*(?:"[^"]*"|''[^'']*''|[^\s;]+)'),
        '$1=<redacted>'
    $protected = $protected -replace (
        '(?i)(--(?:rpc-)?secret|--?cookie|--?token|SESSDATA|bili_jct|' +
        'DedeUserID)\s+(?:"[^"]*"|''[^'']*''|\S+)'),
        '$1 <redacted>'
    return $protected
}

function Get-ProcessTree {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,
        [DateTimeOffset]$NotBeforeUtc = [DateTimeOffset]::MinValue
    )

    if ($IsWindows) {
        $pending = [System.Collections.Generic.Queue[object]]::new()
        $pending.Enqueue([pscustomobject]@{
            processId = $RootProcessId
            depth = 0
        })
        $visited = [System.Collections.Generic.HashSet[int]]::new()
        $result = @()
        while ($pending.Count -gt 0) {
            $parent = $pending.Dequeue()
            if (-not $visited.Add([int]$parent.processId)) {
                continue
            }

            $children = @(
                Get-CimInstance `
                    -ClassName Win32_Process `
                    -Filter "ParentProcessId = $($parent.processId)" `
                    -ErrorAction SilentlyContinue
            )
            foreach ($child in $children) {
                $creationTime = [DateTimeOffset]$child.CreationDate
                if ($creationTime -lt $NotBeforeUtc) {
                    continue
                }

                $result += [pscustomobject]@{
                    processId = [int]$child.ProcessId
                    parentProcessId = [int]$child.ParentProcessId
                    name = [string]$child.Name
                    createdAtUtc = $creationTime.ToUniversalTime().ToString("O")
                    depth = [int]$parent.depth + 1
                    executableName = if (
                        [string]::IsNullOrWhiteSpace([string]$child.ExecutablePath)
                    ) {
                        $null
                    }
                    else {
                        [System.IO.Path]::GetFileName([string]$child.ExecutablePath)
                    }
                    commandLine = Protect-ProcessDiagnosticText `
                        -Value ([string]$child.CommandLine)
                }
                $pending.Enqueue([pscustomobject]@{
                    processId = [int]$child.ProcessId
                    depth = [int]$parent.depth + 1
                })
            }
        }

        return $result
    }

    $rows = @(& ps -eo pid=,ppid=,comm= 2>$null)
    $processes = @(
        foreach ($row in $rows) {
            if ($row -match '^\s*(\d+)\s+(\d+)\s+(.+?)\s*$') {
                $observedProcess = Get-Process `
                    -Id ([int]$Matches[1]) `
                    -ErrorAction SilentlyContinue
                $createdAtUtc = $null
                if ($null -ne $observedProcess) {
                    try {
                        $createdAtUtc = (
                            [DateTimeOffset]$observedProcess.StartTime.ToUniversalTime()
                        ).ToString("O")
                    }
                    catch [System.InvalidOperationException] {
                        $createdAtUtc = $null
                    }
                    finally {
                        $observedProcess.Dispose()
                    }
                }

                [pscustomobject]@{
                    processId = [int]$Matches[1]
                    parentProcessId = [int]$Matches[2]
                    name = $Matches[3]
                    createdAtUtc = $createdAtUtc
                    depth = 0
                    executableName = $Matches[3]
                    commandLine = $null
                }
            }
        }
    )
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    $result = @()
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        foreach ($child in @($processes | Where-Object { $_.parentProcessId -eq $parent })) {
            $result += $child
            $pending.Enqueue($child.processId)
        }
    }

    return $result
}

function Save-ManagedStack {
    param(
        [Parameter(Mandatory)]
        [int]$TargetProcessId,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
        Set-Content -LiteralPath $Destination -Encoding utf8 `
            -Value "dotnet-stack is unavailable. Install it in .tools to capture managed stacks."
        return [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
        }
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:diagnosticsTool
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("report")
    $startInfo.ArgumentList.Add("--process-id")
    $startInfo.ArgumentList.Add(
        $TargetProcessId.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $stackProcess = [System.Diagnostics.Process]::new()
    $stackProcess.StartInfo = $startInfo
    try {
        if (-not $stackProcess.Start()) {
            throw "dotnet-stack did not start."
        }

        $stdoutTask = $stackProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $stackProcess.StandardError.ReadToEndAsync()
        $timedOut = -not $stackProcess.WaitForExit(15000)
        if ($timedOut) {
            $stackProcess.Kill($true)
            $stackProcess.WaitForExit()
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText(
            $Destination,
            $stdout + $stderr,
            [System.Text.UTF8Encoding]::new($false))
        return [pscustomobject]@{
            available = $true
            captured = -not $timedOut -and
                $stackProcess.ExitCode -eq 0 -and
                -not [string]::IsNullOrWhiteSpace($stdout)
            exitCode = $stackProcess.ExitCode
            timedOut = $timedOut
        }
    }
    finally {
        $stackProcess.Dispose()
    }
}

function Save-ProcessEvidence {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Reason,
        [switch]$SkipManagedStack
    )

    $safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'
    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-{2}" -f $Iteration, $Phase, $safeReason)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $threadSnapshot = @()
    if ($IsWindows -and -not $Process.HasExited) {
        try {
            $Process.Refresh()
            foreach ($thread in @($Process.Threads)) {
                $waitReason = $null
                if ($thread.ThreadState -eq [System.Diagnostics.ThreadState]::Wait) {
                    try {
                        $waitReason = $thread.WaitReason.ToString()
                    }
                    catch [System.InvalidOperationException] {
                        $waitReason = "unavailable"
                    }
                }

                $threadSnapshot += [pscustomobject]@{
                    id = $thread.Id
                    state = $thread.ThreadState.ToString()
                    waitReason = $waitReason
                    totalProcessorTimeMs = $thread.TotalProcessorTime.TotalMilliseconds
                }
            }
        }
        catch [System.InvalidOperationException] {
            $threadSnapshot = @()
        }
    }

    $processTree = @(Get-ProcessTree -RootProcessId $Process.Id)
    $stackResult = if ($Process.HasExited -or $SkipManagedStack) {
        [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
        }
    }
    else {
        Save-ManagedStack `
            -TargetProcessId $Process.Id `
            -Destination (Join-Path $directory "managed-stack.txt")
    }
    $evidence = [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = $Reason
        processId = $Process.Id
        processName = if ($Process.HasExited) { $null } else { $Process.ProcessName }
        hasExited = $Process.HasExited
        threads = $threadSnapshot
        processTree = $processTree
        managedStack = $stackResult
    }
    $evidence |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $directory "process-evidence.json") -Encoding utf8
    return [System.IO.Path]::GetRelativePath($runRoot, $directory).
        Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Save-ResidualChildEvidence {
    param(
        [Parameter(Mandatory)]
        [object[]]$Children,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase
    )

    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-residual-children" -f $Iteration, $Phase)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $childEvidence = @()
    $captureErrors = @()
    foreach ($child in $Children) {
        $captureState = "exited-before-capture"
        $processEvidencePath = $null
        $captureErrorType = $null
        $childProcess = $null
        try {
            $childProcess = Get-Process `
                -Id $child.processId `
                -ErrorAction SilentlyContinue
            if ($null -eq $childProcess) {
                $captureState = "exited-before-capture"
            }
            else {
                $actualStart = [DateTimeOffset]$childProcess.StartTime.ToUniversalTime()
                $expectedStart = if (
                    [string]::IsNullOrWhiteSpace([string]$child.createdAtUtc)
                ) {
                    $actualStart
                }
                else {
                    [DateTimeOffset]::Parse(
                        $child.createdAtUtc,
                        [System.Globalization.CultureInfo]::InvariantCulture)
                }
                if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1) {
                    $captureState = "process-identity-changed"
                    $captureErrorType = "ProcessIdentityChanged"
                }
                else {
                    $processEvidencePath = Save-ProcessEvidence `
                        -Process $childProcess `
                        -AssemblyName $AssemblyName `
                        -Iteration $Iteration `
                        -Phase $Phase `
                        -Reason "residual-child-$($child.processId)" `
                        -SkipManagedStack:(
                            [string]$child.name -notmatch
                                '^(?:dotnet|pwsh|testhost|xunit|DownKyi).*\.exe$')
                    $captureState = "captured"
                }
            }
        }
        catch [System.InvalidOperationException] {
            $captureState = "exited-before-capture"
        }
        catch {
            $captureState = "capture-failed"
            $captureErrorType = $_.Exception.GetType().Name
        }
        finally {
            if ($null -ne $childProcess) {
                $childProcess.Dispose()
            }
        }

        if ($null -ne $captureErrorType) {
            $captureErrors += $captureErrorType
        }
        $childEvidence += [pscustomobject]@{
            processId = $child.processId
            createdAtUtc = $child.createdAtUtc
            captureState = $captureState
            processEvidencePath = $processEvidencePath
            errorType = $captureErrorType
        }
    }

    $manifest = [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = "residual-child-process"
        observedChildren = $Children
        childEvidence = $childEvidence
        captureErrors = @($captureErrors | Select-Object -Unique)
    }
    $manifestPath = Join-Path $directory "residual-children.json"
    $manifest |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [pscustomobject]@{
        evidencePath = [System.IO.Path]::GetRelativePath($runRoot, $directory).
            Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        capturedChildCount = @(
            $childEvidence | Where-Object captureState -eq "captured"
        ).Count
        exitedBeforeCaptureCount = @(
            $childEvidence | Where-Object captureState -eq "exited-before-capture"
        ).Count
        errorType = if ($captureErrors.Count -eq 0) {
            $null
        }
        else {
            [string]$captureErrors[0]
        }
    }
}

function Get-LifecycleMarkerReadFailureCategory {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    if ($IsWindows -and $Exception -is [System.IO.IOException]) {
        $nativeErrorCode = $Exception.HResult -band 0xFFFF
        if ($nativeErrorCode -in @(32, 33)) {
            return "contention"
        }
    }

    return "error"
}

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

function Read-TeardownMarker {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [ValidateRange(1, 20)]
        [int]$Attempts = 4,
        [ValidateRange(0, 1000)]
        [int]$RetryDelayMilliseconds = 5
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $lines = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $stream = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                $share)
            try {
                $reader = [System.IO.StreamReader]::new($stream)
                try {
                    $lines = @($reader.ReadToEnd() -split '\r?\n')
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            break
        }
        catch [System.IO.IOException] {
            if ((Get-LifecycleMarkerReadFailureCategory -Exception $_.Exception) -eq
                "contention") {
                $script:markerReadContentionCount++
            }
            else {
                $script:markerReadErrorCount++
                $script:markerReadErrorType = $_.Exception.GetType().Name
            }
        }
        catch [System.UnauthorizedAccessException] {
            $script:markerReadErrorCount++
            $script:markerReadErrorType = $_.Exception.GetType().Name
        }

        if ($attempt -lt $Attempts -and $RetryDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    }

    if ($null -eq $lines) {
        $script:markerReadRetriesExhaustedCount++
        return $null
    }

    $states = @()
    foreach ($line in $lines) {
        if ($line -match '^(started|disposing|disposed)\|(\d+)\|(\d+)$') {
            $states += [pscustomobject]@{
                state = $Matches[1]
                processId = [int]$Matches[2]
                timestamp = [long]$Matches[3]
            }
        }
    }

    $started = @($states | Where-Object state -eq "started" | Select-Object -Last 1)
    $disposing = @($states | Where-Object state -eq "disposing" | Select-Object -Last 1)
    $disposed = @($states | Where-Object state -eq "disposed" | Select-Object -Last 1)
    return [pscustomobject]@{
        states = $states
        started = if ($started.Count -eq 0) { $null } else { $started[0] }
        disposing = if ($disposing.Count -eq 0) { $null } else { $disposing[0] }
        disposed = if ($disposed.Count -eq 0) { $null } else { $disposed[0] }
    }
}

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
        $residualChildren = @(
            Get-ProcessTree `
                -RootProcessId $processId `
                -NotBeforeUtc $processStartedAt
        )
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
            residualChildren = $residualChildren
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
        residualChildCount = $ProcessResult.residualChildren.Count
        residualChildren = @($ProcessResult.residualChildren)
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

$testProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests") `
        -Filter "*.Tests.csproj" `
        -File `
        -Recurse |
        Where-Object {
            $project = $_
            @($AssemblyPattern | Where-Object { $project.BaseName -like $_ }).Count -gt 0
        } |
        Sort-Object BaseName
)
if ($testProjects.Count -eq 0) {
    throw "No xUnit test assemblies were found."
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
        residualChildCount = $selfTestPhase.residualChildCount
        residualChildren = @($selfTestPhase.residualChildren)
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
            residualChildCount = 0
            residualChildren = @()
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
            residualChildCount = 0
            residualChildren = @()
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

        $assemblyInfo = Invoke-IsolatedProcess `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "assembly-info" `
            -FileName "dotnet" `
            -Arguments @($assemblyPath, "-assemblyInfo")
        $phaseResults += New-ProcessPhaseResult -ProcessResult $assemblyInfo

        $discovery = Invoke-IsolatedProcess `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "discovery" `
            -FileName "dotnet" `
            -Arguments @(
                $assemblyPath,
                "-list",
                "full",
                "-automated",
                "-noLogo",
                "-noColor"
            )
        $phaseResults += New-ProcessPhaseResult -ProcessResult $discovery

        $markerPath = Join-Path $rawRoot (
            "$assemblyName/iteration-{0:D4}/execution.lifecycle" -f $iteration)
        $execution = Invoke-IsolatedProcess `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "execution" `
            -FileName "dotnet" `
            -Arguments @(
                $assemblyPath,
                "-automated",
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

        $phaseResults += [pscustomobject]@{
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
            residualChildCount = 0
            residualChildren = @()
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
        $phaseResults += [pscustomobject]@{
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
            residualChildCount = $execution.residualChildren.Count
            residualChildren = @($execution.residualChildren)
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
    }
}

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
$runtime = (& dotnet --version).Trim()
$commitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$workingTreeDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
$report = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    profile = $Profile
    iterations = $resolvedIterations
    runtime = $runtime
    operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    commitSha = $commitSha
    workingTreeDirty = $workingTreeDirty
    testAssemblyCount = $testProjects.Count
    phaseTimeoutSeconds = $PhaseTimeoutSeconds
    slowPhaseThresholdSeconds = $SlowPhaseThresholdSeconds
    slowEvidenceCaptureLeadMilliseconds = $slowEvidenceCaptureLeadMilliseconds
    forensicsSelfTestCaptureLeadValidated =
        $forensicsSelfTestCaptureLeadValidated
    exitThresholdSeconds = $ExitThresholdSeconds
    diagnosticsTool = if ($null -eq $script:diagnosticsTool) {
        "unavailable"
    }
    else {
        [System.IO.Path]::GetFileName($script:diagnosticsTool)
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
    residualChildEvidenceCapturedCount = $residualChildEvidenceCapturedCount
    residualChildEvidenceMissingCount = $residualChildEvidenceMissingCount
    diagnosticCaptureTotalMs = $diagnosticCaptureTotalMs
    markerReadContentionCount = $script:markerReadContentionCount
    markerReadRetriesExhaustedCount = $script:markerReadRetriesExhaustedCount
    markerReadErrorCount = $script:markerReadErrorCount
    markerReadErrorType = $script:markerReadErrorType
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
$jsonPath = Join-Path $runRoot "assembly-lifecycle-report.json"
$markdownPath = Join-Path $runRoot "assembly-lifecycle-report.md"
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$markdown = [System.Collections.Generic.List[string]]::new()
$markdown.Add("# Assembly Lifecycle Stability Report")
$markdown.Add("")
$markdown.Add("- Profile: ``$Profile``")
$markdown.Add("- Iterations per assembly: $resolvedIterations")
$markdown.Add("- Runtime: ``$runtime``")
$markdown.Add("- OS: ``$($report.operatingSystem)``")
$markdown.Add("- Architecture: ``$($report.architecture)``")
$markdown.Add("- Commit: ``$commitSha``")
$markdown.Add("- Working tree dirty: ``$workingTreeDirty``")
$markdown.Add("- Assemblies: $($testProjects.Count)")
$markdown.Add("- Ownership audit: $(if ($ownershipPassed) { 'passed' } else { 'failed' })")
$markdown.Add("- Failed phases: $($failedResults.Count)")
$markdown.Add("- Slow phases: $($slowResults.Count)")
$markdown.Add(
    "- Slow phase evidence: $slowEvidenceCapturedCount captured, " +
    "$slowEvidenceMissingCount missing")
$markdown.Add(
    "- Residual children: $residualChildObservedCount observed across " +
    "$($residualChildResults.Count) phase(s); " +
    "$residualChildEvidenceCapturedCount evidence manifest(s), " +
    "$residualChildEvidenceMissingCount missing")
$markdown.Add("- Diagnostic capture wall time: $diagnosticCaptureTotalMs ms")
$markdown.Add(
    "- Forensics pre-threshold capture self-test: " +
    "$forensicsSelfTestCaptureLeadValidated")
$markdown.Add("- Marker read contentions: $script:markerReadContentionCount")
$markdown.Add("- Marker read retry exhaustion: $script:markerReadRetriesExhaustedCount")
$markdown.Add(
    "- Marker read errors: $script:markerReadErrorCount; " +
    "last type=$script:markerReadErrorType")
$markdown.Add(
    "- Marker reader self-test: executed=$($markerReaderSelfTest.executed), " +
    "passed=$($markerReaderSelfTest.passed), " +
    "contentionObserved=$($markerReaderSelfTest.contentionObserved), " +
    "contentionCount=$($markerReaderSelfTest.contentionCount), " +
    "recovered=$($markerReaderSelfTest.recoveredAfterLockRelease), " +
    "parsed=$($markerReaderSelfTest.markerParsedAfterRecovery), " +
    "error=$($markerReaderSelfTest.errorType), " +
    "contractChecks=$($markerReaderSelfTest.contractChecks.passed)")
$markdown.Add(
    "- Residual child self-test: executed=$($residualChildSelfTest.executed), " +
    "passed=$($residualChildSelfTest.passed), " +
    "observed=$($residualChildSelfTest.childObserved), " +
    "identity=$($residualChildSelfTest.identityCaptured), " +
    "evidence=$($residualChildSelfTest.evidenceManifestWritten), " +
    "classified=$($residualChildSelfTest.failureClassified), " +
    "cleanup=$($residualChildSelfTest.cleanupCompleted), " +
    "redaction=$($residualChildSelfTest.redactionValidated), " +
    "error=$($residualChildSelfTest.errorType)")
$markdown.Add("")
$markdown.Add("| Assembly | Phase | Pass / Runs | Slow / captured | Success | P50 ms | P95 ms | P99 ms | Max ms |")
$markdown.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
foreach ($item in $statistics) {
    $markdown.Add(
        "| $($item.assembly) | $($item.phase) | $($item.passed) / $($item.runs) | " +
        "$($item.slowRuns) / $($item.slowEvidenceCaptured) | " +
        "$([Math]::Round($item.successRate * 100, 2))% | $($item.p50Ms) | " +
        "$($item.p95Ms) | $($item.p99Ms) | $($item.maxMs) |")
}
$markdown.Add("")
$markdown.Add("## Slow Phases")
$markdown.Add("")
if ($slowResults.Count -eq 0) {
    $markdown.Add("None.")
}
else {
    $markdown.Add("| Assembly | Iteration | Phase | Duration ms | Capture ms | Evidence status | Evidence |")
    $markdown.Add("| --- | ---: | --- | ---: | ---: | --- | --- |")
    foreach ($slow in $slowResults) {
        $evidenceText = if ($slow.slowEvidence.Count -eq 0) {
            ""
        }
        else {
            $slow.slowEvidence -join "<br>"
        }
        $markdown.Add(
            "| $($slow.assembly) | $($slow.iteration) | $($slow.phase) | " +
            "$($slow.durationMs) | $($slow.diagnosticCaptureDurationMs) | " +
            "$($slow.slowEvidenceStatus) | $evidenceText |")
    }
}
$markdown.Add("")
$markdown.Add("## Failures")
$markdown.Add("")
if ($failedResults.Count -eq 0) {
    $markdown.Add("None.")
}
else {
    foreach ($failure in $failedResults) {
        $markdown.Add(
            "- ``$($failure.assembly)`` iteration $($failure.iteration), " +
            "``$($failure.phase)``: exit=$($failure.exitCode), " +
            "timeout=$($failure.timedOut), stdoutPolluted=$($failure.stdoutPolluted), " +
            "stderrPolluted=$($failure.stderrPolluted), " +
            "residualChildren=$($failure.residualChildCount), " +
            "failureType=$($failure.failureType), errorType=$($failure.errorType), " +
            "slowEvidence=$($failure.slowEvidenceStatus), " +
            "residualEvidence=$($failure.residualChildEvidenceStatus)")
        foreach ($child in @($failure.residualChildren)) {
            $markdown.Add(
                "  - child pid=$($child.processId), parent=$($child.parentProcessId), " +
                "name=``$($child.name)``, created=$($child.createdAtUtc), " +
                "command=``$($child.commandLine)``")
        }
        foreach ($evidencePath in @($failure.residualChildEvidence)) {
            $markdown.Add("  - residual evidence: ``$evidencePath``")
        }
    }
}
$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

$latestPath = Join-Path $outputRoot "latest-run.txt"
Set-Content -LiteralPath $latestPath -Encoding ascii -Value $runId
Write-Host "Assembly lifecycle report: $markdownPath"
Write-Host "Assemblies: $($testProjects.Count); phase results: $($phaseResults.Count); failures: $($failedResults.Count)"

if (-not $report.successful) {
    throw "Assembly Lifecycle Stability Gate failed."
}
