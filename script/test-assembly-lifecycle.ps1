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
. (Join-Path $PSScriptRoot "delegated-cgroup-scope.ps1")
if (Test-DownKyiDelegatedCgroupScopeRequired) {
    Invoke-DownKyiDelegatedCgroupScope `
        -ScriptPath $PSCommandPath `
        -ArgumentList (ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters)
    return
}
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$solutionPath = Join-Path $repositoryRoot "DownKyi.sln"
$probeProject = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj"
$probeAssembly = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/bin/$Configuration/net10.0/DownKyi.AssemblyLifecycleProbe.dll"
$processSupervisionAssembly = Join-Path $repositoryRoot (
    "tools/DownKyi.ProcessSupervision/bin/$Configuration/net10.0/" +
    "DownKyi.ProcessSupervision.dll")
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
$forensicsSelfTestCaptureDelayMilliseconds = 750
$processCleanupGraceSeconds = 5
$forensicsSelfTestCaptureLeadValidated = $false
$markerReaderSelfTestRequired = $IsWindows -and
    @("PR", "Main", "Rehearsal", "Flaky").Contains($Profile)
$markerReaderSelfTestComplete = $false
$processLeaseSelfTestComplete = $false
$processLeaseSelfTest = [ordered]@{
    required = [bool]$ValidateForensics
    executed = $false
    passed = $false
    parentExited = $false
    ownedTreeRejected = $false
    cleanupCompleted = $false
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

function Get-DiagnosticProcessTreeSnapshot {
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
                        $startTime = $observedProcess.StartTime
                        if ($null -ne $startTime) {
                            $createdAtUtc = (
                                [DateTimeOffset]$startTime.ToUniversalTime()
                            ).ToString("O")
                        }
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
            if (-not $stackProcess.WaitForExit(5000)) {
                throw "dotnet-stack did not terminate within its owned deadline."
            }
        }

        $outputTasks = [Threading.Tasks.Task]::WhenAll($stdoutTask, $stderrTask)
        if (-not $outputTasks.Wait(5000)) {
            throw "dotnet-stack output did not drain within its owned deadline."
        }

        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
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

    $processTree = @(
        Get-DiagnosticProcessTreeSnapshot -RootProcessId $Process.Id)
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

function Save-OwnedTreeEvidence {
    param(
        [Parameter(Mandatory)]
        [object]$Failure,
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
    $manifest = [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = "owned-process-tree-not-quiescent"
        failureKind = $Failure.Kind.ToString()
        supervisorProcessId = $Failure.SupervisorProcessId
        targetProcessId = $Failure.TargetProcessId
        treeQuiescent = $Failure.TreeQuiescent
        ownership = $Failure.Ownership
    }
    $manifestPath = Join-Path $directory "residual-children.json"
    $manifest |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [pscustomobject]@{
        evidencePath = [System.IO.Path]::GetRelativePath($runRoot, $directory).
            Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        errorType = $null
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
        [switch]$HoldForEvidenceCapture,
        [ValidateRange(0, 5000)]
        [int]$EvidenceCaptureDelayMilliseconds = 0,
        [switch]$AuthorizeRepositoryTestAssembly,
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

    $evidenceCaptureLease = if ($HoldForEvidenceCapture) {
        New-EvidenceCaptureLease -Environment $launchEnvironment
    }
    else {
        $null
    }

    $authorization = if ($AuthorizeRepositoryTestAssembly) {
        New-DownKyiTestProcessAuthorization `
            -Arguments $Arguments `
            -RepositoryRoot $repositoryRoot
    }
    else {
        $null
    }
    if ($null -ne $authorization) {
        $authorizationStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $authorizationStartInfo.FileName = $FileName
        foreach ($argument in $Arguments) {
            $authorizationStartInfo.ArgumentList.Add($argument)
        }
        Set-DownKyiTestProcessAuthorization `
            -Authorization $authorization `
            -StartInfo $authorizationStartInfo
        $launchEnvironment["DOWNKYI_CENTRAL_TEST_PIPE"] =
            $authorizationStartInfo.Environment["DOWNKYI_CENTRAL_TEST_PIPE"]
        $launchEnvironment["DOWNKYI_CENTRAL_TEST_TOKEN"] =
            $authorizationStartInfo.Environment["DOWNKYI_CENTRAL_TEST_TOKEN"]
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
        if ($null -ne $authorization) {
            $authorization.Item2.ChildProcessId = $lease.TargetProcessId
            Complete-DownKyiTestProcessAuthorization -Authorization $authorization
        }
        if ($null -ne $evidenceCaptureLease) {
            Start-EvidenceCaptureLease -Lease $evidenceCaptureLease
        }

        $processId = $lease.TargetProcessId
        $waitTask = $lease.WaitAsync()
        while (-not $waitTask.IsCompleted) {
            if (-not $slowEvidenceAttempted -and
                $stopwatch.Elapsed.TotalSeconds -ge $evidenceCaptureThresholdSeconds) {
                $slowEvidenceTriggeredBeforeThreshold =
                    $stopwatch.Elapsed.TotalSeconds -lt $EvidenceThresholdSeconds
                $slowEvidenceAttempted = $true
                $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $diagnosticProcess = $null
                try {
                    if ($EvidenceCaptureDelayMilliseconds -gt 0) {
                        Start-Sleep -Milliseconds $EvidenceCaptureDelayMilliseconds
                    }
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
                    if ($null -ne $evidenceCaptureLease) {
                        try {
                            Complete-EvidenceCaptureLease -Lease $evidenceCaptureLease
                        }
                        catch {
                            $completionErrorType = $_.Exception.GetType().Name
                            $slowEvidenceErrorType = if (
                                [string]::IsNullOrWhiteSpace($slowEvidenceErrorType)) {
                                $completionErrorType
                            }
                            else {
                                "$slowEvidenceErrorType;$completionErrorType"
                            }
                            $slowEvidenceStatus = "capture-failed"
                        }
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

            Start-Sleep -Milliseconds 25
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
            ownedProcessCleanupFailures = @(
                if ($null -ne $ownedFailure) {
                    $ownedFailure.CleanupFailures | ForEach-Object {
                        $_.GetType().Name
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
            $lease.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    try {
        Close-EvidenceCaptureLease -Lease $evidenceCaptureLease
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    try {
        Close-DownKyiTestProcessAuthorization -Authorization $authorization
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
        -LifecycleMarkerPath $LifecycleMarkerPath `
        -AuthorizeRepositoryTestAssembly
}

function New-EvidenceCaptureLease {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Environment
    )

    $pipe = [IO.Pipes.AnonymousPipeServerStream]::new(
        [IO.Pipes.PipeDirection]::Out,
        [IO.HandleInheritability]::Inheritable)
    $Environment["DOWNKYI_FORENSICS_CAPTURE_PIPE"] =
        $pipe.GetClientHandleAsString()
    return [pscustomobject]@{
        Pipe = $pipe
        Started = $false
        Closed = $false
    }
}

function Start-EvidenceCaptureLease {
    param(
        [Parameter(Mandatory)]
        [object]$Lease
    )

    if ($Lease.Started -or $Lease.Closed) {
        throw "Evidence capture lease is not startable."
    }

    $Lease.Pipe.DisposeLocalCopyOfClientHandle()
    $Lease.Started = $true
}

function Complete-EvidenceCaptureLease {
    param(
        [Parameter(Mandatory)]
        [object]$Lease
    )

    if (-not $Lease.Started -or $Lease.Closed) {
        throw "Evidence capture lease is not completable."
    }

    try {
        $Lease.Pipe.WriteByte(0xA5)
        $Lease.Pipe.Flush()
    }
    finally {
        $Lease.Closed = $true
        $Lease.Pipe.Dispose()
    }
}

function Close-EvidenceCaptureLease {
    param(
        [object]$Lease
    )

    if ($null -ne $Lease -and -not $Lease.Closed) {
        $Lease.Closed = $true
        $Lease.Pipe.Dispose()
    }
}

$reporterContractSelfTestPassed = Test-XunitReporterContractMutation
if (-not $reporterContractSelfTestPassed) {
    throw "xUnit reporter contract mutation self-test did not fail closed."
}

function Test-JsonProtocol {
    param(
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [AllowEmptyString()]
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
    $ownedCleanupComplete = $ProcessResult.ownedProcessCleanupFailures.Count -eq 0
    $success = $ProcessResult.exitCode -eq 0 -and
        -not $ProcessResult.timedOut -and
        $ProcessResult.ownedTreeQuiescent -and
        $ownedCleanupComplete -and
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
    elseif (-not $ownedCleanupComplete) {
        "ProcessCleanupFailed"
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
    elseif ($failureType -eq "ProcessCleanupFailed") {
        [string]$ProcessResult.ownedProcessCleanupFailures[0]
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
        ownedTreeQuiescent = $ProcessResult.ownedTreeQuiescent
        ownedProcessFailureKind = $ProcessResult.ownedProcessFailureKind
        ownedProcessCleanupFailures = @($ProcessResult.ownedProcessCleanupFailures)
        processOwnership = $ProcessResult.processOwnership
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
if (-not (Test-Path -LiteralPath $processSupervisionAssembly -PathType Leaf)) {
    throw "Process supervision assembly was not built: $processSupervisionAssembly"
}
[Reflection.Assembly]::LoadFrom($processSupervisionAssembly) | Out-Null

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
            $selfTestAssembly
        ) `
        -LifecycleMarkerPath $selfTestMarker `
        -HoldForEvidenceCapture `
        -EvidenceCaptureDelayMilliseconds $forensicsSelfTestCaptureDelayMilliseconds `
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

    $processLeaseSelfTestStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $processLeaseSelfTest.executed = $true
    $processLeaseProbe = $null
    $processLeaseProbePhase = $null
    $processLeaseReadyPath = Join-Path $rawRoot "Gate.ProcessLease/parent-exit.json"
    try {
        New-Item -ItemType Directory -Force `
            -Path ([System.IO.Path]::GetDirectoryName($processLeaseReadyPath)) |
            Out-Null
        $processLeaseProbe = Invoke-IsolatedProcess `
            -AssemblyName "Gate.ProcessLease" `
            -Iteration 1 `
            -Phase "owned-tree-self-test" `
            -FileName "dotnet" `
            -Arguments @(
                $processSupervisionAssembly,
                "--exit-with-owned-descendant",
                $processLeaseReadyPath
            ) `
            -OperationTimeoutSeconds 1 `
            -EvidenceThresholdSeconds 60
        $processLeaseProbePhase = New-ProcessPhaseResult `
            -ProcessResult $processLeaseProbe
        $processLeaseSelfTest.parentExited =
            Test-Path -LiteralPath $processLeaseReadyPath -PathType Leaf
        $processLeaseSelfTest.ownedTreeRejected =
            -not $processLeaseProbePhase.success -and
            $processLeaseProbePhase.failureType -eq "ResidualChildProcess" -and
            $processLeaseProbe.ownedProcessFailureKind -eq "OwnedTreeNotQuiescent"
        $processLeaseSelfTest.cleanupCompleted =
            $processLeaseProbe.ownedProcessCleanupFailures.Count -eq 0
    }
    catch {
        $processLeaseSelfTest.errorType = $_.Exception.GetType().Name
    }
    finally {
        Remove-Item -LiteralPath $processLeaseReadyPath -Force -ErrorAction SilentlyContinue
        $processLeaseSelfTestStopwatch.Stop()
    }

    $processLeaseSelfTest.passed =
        $processLeaseSelfTest.parentExited -and
        $processLeaseSelfTest.ownedTreeRejected -and
        $processLeaseSelfTest.cleanupCompleted -and
        $null -eq $processLeaseSelfTest.errorType
    $processLeaseSelfTestComplete = $processLeaseSelfTest.passed
    if (-not $processLeaseSelfTestComplete -and
        $null -eq $processLeaseSelfTest.errorType) {
        $processLeaseSelfTest.errorType = "ContractNotSatisfied"
    }

    $phaseResults += [pscustomobject]@{
        assembly = "Gate.ProcessLease"
        iteration = 1
        phase = "owned-tree-self-test"
        processId = if ($null -eq $processLeaseProbe) {
            $PID
        }
        else {
            $processLeaseProbe.processId
        }
        success = $processLeaseSelfTestComplete
        failureType = if ($processLeaseSelfTestComplete) {
            $null
        }
        else {
            "ProcessLeaseSelfTestFailed"
        }
        errorType = $processLeaseSelfTest.errorType
        exitCode = if ($processLeaseSelfTestComplete) { 0 } else { 1 }
        durationMs = [Math]::Round(
            $processLeaseSelfTestStopwatch.Elapsed.TotalMilliseconds,
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
            if ($null -ne $processLeaseProbe) {
                $processLeaseProbe.residualChildEvidence
            }
        )
        residualChildEvidenceStatus = if ($null -eq $processLeaseProbe) {
            "not-triggered"
        }
        else {
            $processLeaseProbe.residualChildEvidenceStatus
        }
        residualChildEvidenceErrorType = if ($null -eq $processLeaseProbe) {
            $null
        }
        else {
            $processLeaseProbe.residualChildEvidenceErrorType
        }
        stdoutPath = if ($null -eq $processLeaseProbe) {
            $null
        }
        else {
            $processLeaseProbe.stdoutPath
        }
        stderrPath = if ($null -eq $processLeaseProbe) {
            $null
        }
        else {
            $processLeaseProbe.stderrPath
        }
        evidence = @(
            if ($null -ne $processLeaseProbe) {
                $processLeaseProbe.evidence
            }
        )
        slowEvidence = @()
        exitEvidence = @()
        timeoutEvidence = @()
        diagnosticCaptureDurationMs = if ($null -eq $processLeaseProbe) {
            0.0
        }
        else {
            $processLeaseProbe.diagnosticCaptureDurationMs
        }
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold = $false
    }

    if ($IsWindows) {
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
        $markerValid = $null -ne $marker -and
            $null -ne $marker.started -and
            $null -ne $marker.disposing -and
            $null -ne $marker.disposed -and
            $marker.started.processId -eq $marker.disposing.processId -and
            $marker.started.processId -eq $marker.disposed.processId
        $testRootRemoved = $false
        $teardownDuration = 0.0
        $exitDuration = [double]$execution.durationMs
        if ($markerValid -and $null -ne $execution.processExitedAtUnixMs) {
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
        $exitSucceeded = $null -ne $execution.processExitedAtUnixMs -and
            $execution.exitCode -eq 0 -and
            -not $execution.timedOut -and
            $execution.ownedTreeQuiescent -and
            $execution.ownedProcessCleanupFailures.Count -eq 0 -and
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
$processLeaseSelfTestContractPassed =
    -not $processLeaseSelfTest.required -or
    $processLeaseSelfTestComplete
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
    schemaVersion = 3
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
    forensicsSelfTestCaptureDelayMilliseconds =
        $forensicsSelfTestCaptureDelayMilliseconds
    processCleanupGraceSeconds = $processCleanupGraceSeconds
    forensicsSelfTestCaptureLeadValidated =
        $forensicsSelfTestCaptureLeadValidated
    reporterContractSelfTestPassed = $reporterContractSelfTestPassed
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
        $processLeaseSelfTestContractPassed
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
    processLeaseSelfTestPassed = if ($processLeaseSelfTest.executed) {
        $processLeaseSelfTestComplete
    }
    else {
        $null
    }
    processLeaseSelfTest = $processLeaseSelfTest
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
$markdown.Add("- Reporter contract mutation self-test: $reporterContractSelfTestPassed")
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
    "- Process lease self-test: executed=$($processLeaseSelfTest.executed), " +
    "passed=$($processLeaseSelfTest.passed), " +
    "parentExited=$($processLeaseSelfTest.parentExited), " +
    "ownedTreeRejected=$($processLeaseSelfTest.ownedTreeRejected), " +
    "cleanup=$($processLeaseSelfTest.cleanupCompleted), " +
    "error=$($processLeaseSelfTest.errorType)")
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
