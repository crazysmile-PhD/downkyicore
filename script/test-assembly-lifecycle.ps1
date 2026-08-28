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
$hostedCollectorStartupAllowanceMilliseconds = 3000
$slowEvidenceCaptureLeadMilliseconds =
    $hostedCollectorStartupAllowanceMilliseconds
$forensicsSelfTestCaptureDelayMilliseconds = 750
$forensicsSelfTestPositiveCaptureThresholdSeconds = 1.25
$forensicsSelfTestEvidenceThresholdSeconds =
    ($slowEvidenceCaptureLeadMilliseconds / 1000) +
    $forensicsSelfTestPositiveCaptureThresholdSeconds
$processCleanupGraceSeconds = 5
$forensicsCaptureWindowMilliseconds = 15000
$forensicsCaptureCleanupWindowMilliseconds = $processCleanupGraceSeconds * 1000
$forensicsSelfTestCaptureLeadValidated = $false
$forensicsSelfTestPositiveCaptureThresholdValidated = $false
$forensicsSelfTestObservedCaptureThresholdSeconds = $null
$forensicsSelfTestCaptureCompletedBeforeTargetExitValidated = $false
$forensicsSelfTestEvidenceHoldValidated = $false
$forensicsSelfTestReleaseOrderingMutationValidated = $false
$forensicsSelfTestReleaseOrderingMutation = $null
$slowEvidenceOrderingSelfTestPassed = $false
$slowEvidenceOrderingSelfTest = $null
$forensicsCollectorCaptureWindowSelfTestPassed = $false
$forensicsCollectorCaptureWindowSelfTest = $null
$forensicsCollectorCleanupReportSelfTestPassed = $false
$forensicsCollectorCleanupReportSelfTest = $null
$forensicsCollectorInterruptedStackSelfTestPassed = $false
$forensicsCollectorInterruptedStackSelfTest = $null
$dotnetStackAttachStallSelfTestRequired = [bool]($ValidateForensics -and $IsWindows)
$dotnetStackAttachStallSelfTestPassed = $false
$dotnetStackAttachStallSelfTest = $null
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
    diagnosticObserverMissedDescendant = $false
    observerFailurePreserved = $false
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

function Get-TransitionBudgetWaitMilliseconds {
    param(
        [Parameter(Mandatory)]
        [object]$Budget,
        [switch]$Cleanup
    )

    $remaining = if ($Cleanup) {
        $Budget.RemainingCleanup
    }
    else {
        $Budget.RemainingOperation
    }
    if ($remaining -le [TimeSpan]::Zero) {
        throw [TimeoutException]::new(
            "Forensics observer exhausted the process owner's transition budget.")
    }

    return [Math]::Max(
        1,
        [Math]::Min(
            [int]::MaxValue,
            [Math]::Floor($remaining.TotalMilliseconds)))
}

function Invoke-OwnedDiagnosticCollector {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [hashtable]$Environment = @{},
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

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
        $false)
    $request = [DownKyi.ProcessSupervision.DiagnosticCollectorRequest]::new(
        $launchSpec,
        $CaptureWindow)
    return [DownKyi.ProcessSupervision.OwnedDiagnosticCollector]::CollectAsync(
            $request,
            $CancellationToken).
        GetAwaiter().GetResult()
}

function Get-DiagnosticCollectorExecutionFailure {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    $pending = [Collections.Generic.Queue[Exception]]::new()
    $pending.Enqueue($Exception)
    while ($pending.Count -gt 0) {
        $candidate = $pending.Dequeue()
        if ($candidate -is
            [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException]) {
            return $candidate
        }

        if ($candidate -is [AggregateException]) {
            foreach ($innerFailure in $candidate.InnerExceptions) {
                $pending.Enqueue($innerFailure)
            }
        }
        elseif ($null -ne $candidate.InnerException) {
            $pending.Enqueue($candidate.InnerException)
        }
    }

    return $null
}

function ConvertTo-DiagnosticCollectorFailureReport {
    param(
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException]$Exception
    )

    $cleanupFailures = @(
        $Exception.CleanupFailures | ForEach-Object {
            [pscustomobject]@{
                kind = $_.Kind.ToString()
                causeType = $_.Cause.GetType().Name
            }
        }
    )
    if ($env:DOWNKYI_TEST_MUTATE_FORENSICS_CLEANUP_REPORT -eq "1") {
        $cleanupFailures = @()
    }

    return [pscustomobject]@{
        status = "capture-failed"
        evidencePath = $null
        errorType = $Exception.GetType().Name
        collectorFailureKind = $Exception.Failure.Kind.ToString()
        collectorEvidence = $Exception.Failure.Evidence
        collectorCleanupFailures = $cleanupFailures
    }
}

function New-DiagnosticCollectorCleanupFailureFixture {
    $timeline = [DownKyi.ProcessSupervision.DiagnosticCollectorTimeline]::new(
        [DownKyi.ProcessSupervision.DiagnosticCollectorTransitionEvidence[]]@())
    $evidence = [DownKyi.ProcessSupervision.DiagnosticCollectorEvidence]::new(
        $true,
        $false,
        $false,
        $true,
        $true,
        $null,
        "fixture stdout",
        "fixture stderr",
        $timeline)
    $primaryFailure = [DownKyi.ProcessSupervision.DiagnosticCollectorFailure]::new(
        [DownKyi.ProcessSupervision.DiagnosticCollectorFailureKind]::ExecutionFailed,
        $evidence,
        [InvalidOperationException]::new("fixture execution failure"))
    $cleanupFailures = [Collections.Generic.List[
        DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailure]]::new()
    $cleanupFailures.Add(
        [DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailure]::new(
            [DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailureKind]::TerminateFailed,
            [UnauthorizedAccessException]::new("fixture terminate failure")))
    $cleanupFailures.Add(
        [DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailure]::new(
            [DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailureKind]::ReapDeadlineExceeded,
            [TimeoutException]::new("fixture reap deadline")))

    $bindingFlags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic
    $constructor = @(
        [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException].
            GetConstructors($bindingFlags) |
            Where-Object { $_.GetParameters().Count -eq 2 }
    ) | Select-Object -First 1
    if ($null -eq $constructor) {
        throw "Diagnostic collector execution exception constructor was not found."
    }

    $arguments = [object[]]::new(2)
    $arguments[0] = $primaryFailure
    $arguments[1] = $cleanupFailures
    return $constructor.Invoke($arguments)
}

function Test-DiagnosticCollectorCleanupFailureReport {
    $fixture = New-DiagnosticCollectorCleanupFailureFixture
    $report = ConvertTo-DiagnosticCollectorFailureReport -Exception $fixture
    $serialized = $report |
        ConvertTo-Json -Depth 8 -Compress |
        ConvertFrom-Json
    $cleanupFailures = @($serialized.collectorCleanupFailures)
    $passed = $serialized.errorType -eq
            "DiagnosticCollectorExecutionException" -and
        $serialized.collectorFailureKind -eq "ExecutionFailed" -and
        $serialized.collectorEvidence.StandardOutput -eq "fixture stdout" -and
        $serialized.collectorEvidence.StandardError -eq "fixture stderr" -and
        $cleanupFailures.Count -eq 2 -and
        $cleanupFailures[0].kind -eq "TerminateFailed" -and
        $cleanupFailures[0].causeType -eq "UnauthorizedAccessException" -and
        $cleanupFailures[1].kind -eq "ReapDeadlineExceeded" -and
        $cleanupFailures[1].causeType -eq "TimeoutException"
    return [pscustomobject]@{
        passed = $passed
        failureKind = $serialized.collectorFailureKind
        evidence = $serialized.collectorEvidence
        cleanupFailures = $cleanupFailures
    }
}

function New-DiagnosticCollectorInterruptedStackFixture {
    param(
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.DiagnosticCollectorFailureKind]$FailureKind,
        [AllowEmptyString()]
        [string]$StandardOutput
    )

    $stackOutputTransition =
        [DownKyi.ProcessSupervision.DiagnosticCollectorTransitionEvidence]::new(
            [DownKyi.ProcessSupervision.DiagnosticCollectorTransition]::StackOutputFirstByte,
            [DownKyi.ProcessSupervision.DiagnosticCollectorTransitionState]::Observed,
            100.0,
            "fixture stack output")
    $timeline = [DownKyi.ProcessSupervision.DiagnosticCollectorTimeline]::new(
        [DownKyi.ProcessSupervision.DiagnosticCollectorTransitionEvidence[]]@(
            $stackOutputTransition))
    $evidence = [DownKyi.ProcessSupervision.DiagnosticCollectorEvidence]::new(
        $true,
        $true,
        $true,
        $true,
        $false,
        $null,
        $StandardOutput,
        "",
        $timeline)
    $primaryFailure = [DownKyi.ProcessSupervision.DiagnosticCollectorFailure]::new(
        $FailureKind,
        $evidence,
        [OperationCanceledException]::new("fixture target-exit cancellation"))
    $cleanupFailures = [Collections.Generic.List[
        DownKyi.ProcessSupervision.DiagnosticCollectorCleanupFailure]]::new()

    $bindingFlags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic
    $constructor = @(
        [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException].
            GetConstructors($bindingFlags) |
            Where-Object { $_.GetParameters().Count -eq 2 }
    ) | Select-Object -First 1
    if ($null -eq $constructor) {
        throw "Diagnostic collector execution exception constructor was not found."
    }

    $arguments = [object[]]::new(2)
    $arguments[0] = $primaryFailure
    $arguments[1] = $cleanupFailures
    return $constructor.Invoke($arguments)
}

function Test-DiagnosticCollectorFailureHasCapturedStack {
    param(
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException]$Exception
    )

    $evidence = $Exception.Failure.Evidence
    $stackOutputTransition = Get-DiagnosticCollectorTransition `
        -Evidence $evidence `
        -Name "StackOutputFirstByte"
    return $Exception.Failure.Kind -eq
            [DownKyi.ProcessSupervision.DiagnosticCollectorFailureKind]::CallerCancelled -and
        $Exception.CleanupFailures.Count -eq 0 -and
        $evidence.Started -and
        $evidence.Exited -and
        $evidence.Reaped -and
        $evidence.StreamsDrained -and
        -not $evidence.TimedOut -and
        $null -ne $stackOutputTransition -and
        $stackOutputTransition.State -eq
            [DownKyi.ProcessSupervision.DiagnosticCollectorTransitionState]::Observed -and
        $evidence.StandardOutput -match '(?m)^Thread \(0x[0-9A-Fa-f]+\):\r?$'
}

function Test-DiagnosticCollectorInterruptedStackPolicy {
    $capturedStack = New-DiagnosticCollectorInterruptedStackFixture `
        -FailureKind CallerCancelled `
        -StandardOutput "Thread (0x1):`r`n  fixture frame`r`n"
    $emptyCancellation = New-DiagnosticCollectorInterruptedStackFixture `
        -FailureKind CallerCancelled `
        -StandardOutput ""
    $unrelatedFailure = New-DiagnosticCollectorInterruptedStackFixture `
        -FailureKind ExecutionFailed `
        -StandardOutput "Thread (0x1):`r`n  fixture frame`r`n"
    $capturedStackAccepted =
        Test-DiagnosticCollectorFailureHasCapturedStack -Exception $capturedStack
    $emptyCancellationRejected = -not (
        Test-DiagnosticCollectorFailureHasCapturedStack -Exception $emptyCancellation)
    $unrelatedFailureRejected = -not (
        Test-DiagnosticCollectorFailureHasCapturedStack -Exception $unrelatedFailure)
    return [pscustomobject]@{
        passed = $capturedStackAccepted -and
            $emptyCancellationRejected -and
            $unrelatedFailureRejected
        capturedStackAccepted = $capturedStackAccepted
        emptyCancellationRejected = $emptyCancellationRejected
        unrelatedFailureRejected = $unrelatedFailureRejected
    }
}

function Test-OwnedDiagnosticCollectorCaptureWindow {
    param(
        [Parameter(Mandatory)]
        [string]$ProcessSupervisionAssembly
    )

    $budget = [DownKyi.ProcessSupervision.TransitionBudget]::Start(
        [TimeSpan]::FromSeconds(5),
        [TimeSpan]::FromSeconds(2))
    $operationAllowance = if (
        $env:DOWNKYI_TEST_MUTATE_FORENSICS_CAPTURE_BUDGET -eq "1") {
        $budget.RemainingOperation
    }
    elseif ($env:DOWNKYI_TEST_MUTATE_FORENSICS_STARTUP_WINDOW -eq "1") {
        [TimeSpan]::FromMilliseconds(1)
    }
    else {
        [TimeSpan]::FromMilliseconds(
            $hostedCollectorStartupAllowanceMilliseconds)
    }
    $captureWindow = $budget.AllocateDiagnosticCollectorWindow(
        $operationAllowance,
        [TimeSpan]::FromSeconds(1))
    $readyPath = Join-Path ([System.IO.Path]::GetTempPath()) (
        "downkyi-collector-ready-{0}.json" -f [Guid]::NewGuid().ToString("N"))
    $probeArgument = if (
        $env:DOWNKYI_TEST_MUTATE_FORENSICS_EARLY_READY -eq "1") {
        "--collector-publish-before-block"
    }
    else {
        "--collector-block-with-ready"
    }
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $unexpectedFailureType = $null
    $readyEvidence = $null
    $readyEvidenceErrorType = $null
    try {
        $null = Invoke-OwnedDiagnosticCollector `
            -FileName "dotnet" `
            -Arguments @($ProcessSupervisionAssembly, $probeArgument, $readyPath) `
            -CaptureWindow $captureWindow
    }
    catch {
        $failure = Get-DiagnosticCollectorExecutionFailure `
            -Exception $_.Exception
        if ($null -eq $failure) {
            $unexpectedFailureType = $_.Exception.GetType().Name
        }
    }
    finally {
        $stopwatch.Stop()
    }

    try {
        if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
            $readyEvidence = Get-Content -LiteralPath $readyPath -Raw |
                ConvertFrom-Json
        }
    }
    catch {
        $readyEvidenceErrorType = $_.Exception.GetType().Name
    }
    finally {
        Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
    }

    $operationDeadlineKind =
        [DownKyi.ProcessSupervision.DiagnosticCollectorFailureKind]::OperationDeadlineExceeded
    $contractChecks = [ordered]@{
        typedFailureObserved = $null -ne $failure
        operationDeadlinePreserved = $null -ne $failure -and
            $failure.Failure.Kind -eq $operationDeadlineKind
        collectorStarted = $null -ne $failure -and
            $failure.Failure.Evidence.Started
        blockingTaskEstablished = $null -ne $readyEvidence -and
            $readyEvidence.BlockingTaskEstablished -eq $true
        readyProcessIdValid = $null -ne $readyEvidence -and
            $readyEvidence.ProcessId -gt 0
        stdoutMarkerPreserved = $null -ne $failure -and
            $failure.Failure.Evidence.StandardOutput.Contains(
                "collector-before-block-stdout")
        stderrMarkerPreserved = $null -ne $failure -and
            $failure.Failure.Evidence.StandardError.Contains(
                "collector-before-block-stderr")
        authoritativeReapCompleted = $null -ne $failure -and
            $failure.Failure.Evidence.Reaped
        streamsDrained = $null -ne $failure -and
            $failure.Failure.Evidence.StreamsDrained
        cleanupSucceeded = $null -ne $failure -and
            $failure.CleanupFailures.Count -eq 0
        elapsedBounded = $stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(4)
        parentBudgetPreserved =
            $budget.RemainingOperation -gt [TimeSpan]::FromSeconds(1)
    }
    $passed = -not ($contractChecks.Values -contains $false)
    return [pscustomobject]@{
        passed = $passed
        probeArgument = $probeArgument
        operationAllowanceMilliseconds = [Math]::Round(
            $operationAllowance.TotalMilliseconds,
            3)
        failureKind = if ($null -eq $failure) {
            $null
        }
        else {
            $failure.Failure.Kind.ToString()
        }
        evidence = if ($null -eq $failure) { $null } else { $failure.Failure.Evidence }
        cleanupFailures = @(
            if ($null -ne $failure) {
                $failure.CleanupFailures | ForEach-Object {
                    [pscustomobject]@{
                        kind = $_.Kind.ToString()
                        causeType = $_.Cause.GetType().Name
                    }
                }
            }
        )
        unexpectedFailureType = $unexpectedFailureType
        readyEvidence = $readyEvidence
        readyEvidenceErrorType = $readyEvidenceErrorType
        contractChecks = [pscustomobject]$contractChecks
        elapsedMilliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        parentRemainingOperationMilliseconds = [Math]::Round(
            $budget.RemainingOperation.TotalMilliseconds,
            3)
    }
}

function Wait-DiagnosticFixturePublication {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [object]$Budget
    )

    $directory = Split-Path -Parent $Path
    $watcher = [System.IO.FileSystemWatcher]::new(
        $directory,
        [System.IO.Path]::GetFileName($Path))
    $watcher.NotifyFilter = [System.IO.NotifyFilters]::FileName
    $watcher.EnableRaisingEvents = $true
    try {
        while (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            $waitMilliseconds = Get-TransitionBudgetWaitMilliseconds -Budget $Budget
            $change = $watcher.WaitForChanged(
                [System.IO.WatcherChangeTypes]::All,
                $waitMilliseconds)
            if ($change.TimedOut) {
                throw [TimeoutException]::new(
                    "The diagnostic fixture did not publish before the owner deadline.")
            }
        }

        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    finally {
        $watcher.Dispose()
    }
}

function Get-DiagnosticCollectorTransition {
    param(
        [Parameter(Mandatory)]
        [object]$Evidence,
        [Parameter(Mandatory)]
        [string]$Name
    )

    return @(
        $Evidence.Timeline.Transitions |
            Where-Object { $_.Transition.ToString() -eq $Name }
    ) | Select-Object -First 1
}

function Test-DotnetStackAttachStall {
    param(
        [Parameter(Mandatory)]
        [string]$ProcessSupervisionAssembly,
        [Parameter(Mandatory)]
        [string]$DiagnosticsTool
    )

    $fixtureDirectory = Join-Path $rawRoot "Gate.DotnetStackAttach"
    New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
    $readyPath = Join-Path $fixtureDirectory "diagnostics-ready.json"
    $connectedPath = Join-Path $fixtureDirectory "diagnostics-connected.json"
    $budget = [DownKyi.ProcessSupervision.TransitionBudget]::Start(
        [TimeSpan]::FromSeconds(10),
        [TimeSpan]::FromSeconds(2))
    $targetEnvironment = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $targetEnvironment["DOTNET_EnableDiagnostics"] = "0"
    $targetLaunch = [DownKyi.ProcessSupervision.LaunchSpec]::new(
        "dotnet",
        [string[]]@(
            $ProcessSupervisionAssembly,
            "--diagnostic-ipc-stall-with-ready",
            $readyPath,
            $connectedPath),
        $repositoryRoot,
        $targetEnvironment,
        $true)
    $targetLease = $null
    $failure = $null
    $unexpectedFailureType = $null
    $targetCleanupErrorType = $null
    $ready = $null
    $connected = $null
    $collectorRequestCreatedAtUnixMilliseconds = $null
    $typedOutcomeReturnedAtUnixMilliseconds = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $targetLease = [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
                $targetLaunch,
                $budget).
            GetAwaiter().GetResult()
        $ready = Wait-DiagnosticFixturePublication -Path $readyPath -Budget $budget
        $captureWindow = $budget.AllocateDiagnosticCollectorWindow(
            [TimeSpan]::FromMilliseconds(
                $hostedCollectorStartupAllowanceMilliseconds),
            [TimeSpan]::FromSeconds(1))
        $collectorRequestCreatedAtUnixMilliseconds =
            [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        try {
            $null = Invoke-OwnedDiagnosticCollector `
                -FileName $DiagnosticsTool `
                -Arguments @(
                    "report",
                    "--process-id",
                    $targetLease.TargetProcessId.ToString(
                        [System.Globalization.CultureInfo]::InvariantCulture)) `
                -CaptureWindow $captureWindow `
                -Environment @{
                    TEMP = $fixtureDirectory
                    TMP = $fixtureDirectory
                }
        }
        catch {
            $failure = Get-DiagnosticCollectorExecutionFailure `
                -Exception $_.Exception
            if ($null -eq $failure) {
                $unexpectedFailureType = $_.Exception.GetType().Name
            }
        }
        $typedOutcomeReturnedAtUnixMilliseconds =
            [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $connected = Wait-DiagnosticFixturePublication `
            -Path $connectedPath `
            -Budget $budget
    }
    catch {
        if ($null -eq $unexpectedFailureType) {
            $unexpectedFailureType = $_.Exception.GetType().Name
        }
    }
    finally {
        $stopwatch.Stop()
        if ($null -ne $targetLease) {
            try {
                $null = $targetLease.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }
            catch {
                $targetCleanupErrorType = $_.Exception.GetType().Name
            }
        }
    }

    $evidence = if ($null -eq $failure) {
        $null
    }
    else {
        $failure.Failure.Evidence
    }
    $targetAttach = if ($null -eq $evidence) {
        $null
    }
    else {
        Get-DiagnosticCollectorTransition -Evidence $evidence -Name "TargetAttachBegan"
    }
    $firstProgress = if ($null -eq $evidence) {
        $null
    }
    else {
        Get-DiagnosticCollectorTransition -Evidence $evidence -Name "FirstObservableProgress"
    }
    $stackCapture = if ($null -eq $evidence) {
        $null
    }
    else {
        Get-DiagnosticCollectorTransition -Evidence $evidence -Name "StackCaptureBegan"
    }
    $stackOutput = if ($null -eq $evidence) {
        $null
    }
    else {
        Get-DiagnosticCollectorTransition -Evidence $evidence -Name "StackOutputFirstByte"
    }
    $typedOutcome = if ($null -eq $evidence) {
        $null
    }
    else {
        Get-DiagnosticCollectorTransition -Evidence $evidence -Name "TypedOutcomeReturned"
    }
    $traceFiles = @(Get-ChildItem -LiteralPath $fixtureDirectory -Filter "*.nettrace" -File)
    $targetProcessId = if ($null -eq $targetLease) {
        $null
    }
    else {
        $targetLease.TargetProcessId
    }
    $failureKind = if ($null -eq $failure) {
        $null
    }
    else {
        $failure.Failure.Kind.ToString()
    }
    $contractChecks = [ordered]@{
        targetListening = $null -ne $ready -and $ready.Listening -eq $true
        diagnosticsConnectionAccepted = $null -ne $connected -and
            $connected.ProcessId -eq $targetProcessId -and
            $connected.ConnectedAfterMilliseconds -ge 0 -and
            $null -ne $connected.ConnectedAtUnixMilliseconds
        typedDeadline = $null -ne $failure -and
            $failure.Failure.Kind.ToString() -eq "OperationDeadlineExceeded"
        collectorStarted = $null -ne $evidence -and $evidence.Started
        collectorReaped = $null -ne $evidence -and $evidence.Reaped
        streamsDrained = $null -ne $evidence -and $evidence.StreamsDrained
        cleanupSucceeded = $null -ne $failure -and $failure.CleanupFailures.Count -eq 0
        attachOwnerBoundaryRecorded = $null -ne $targetAttach -and
            $targetAttach.State.ToString() -eq "NotObservable"
        connectionAcceptedDuringCollector = $null -ne $connected -and
            $null -ne $collectorRequestCreatedAtUnixMilliseconds -and
            $null -ne $typedOutcomeReturnedAtUnixMilliseconds -and
            $connected.ConnectedAtUnixMilliseconds -ge
                $collectorRequestCreatedAtUnixMilliseconds -and
            $connected.ConnectedAtUnixMilliseconds -le
                $typedOutcomeReturnedAtUnixMilliseconds
        sessionDidNotStart = $traceFiles.Count -eq 0 -and
            $null -ne $stackCapture -and
            $stackCapture.State.ToString() -eq "NotObservable"
        noToolProgress = $null -ne $firstProgress -and
            $firstProgress.State.ToString() -eq "NotObserved"
        noStackOutput = $null -ne $stackOutput -and
            $stackOutput.State.ToString() -eq "NotObserved"
        windowConsumedWithoutProgress =
            $null -ne $collectorRequestCreatedAtUnixMilliseconds -and
            $null -ne $typedOutcomeReturnedAtUnixMilliseconds -and
            ($typedOutcomeReturnedAtUnixMilliseconds -
                $collectorRequestCreatedAtUnixMilliseconds) -ge 2900
        parentBudgetPreserved = $budget.RemainingOperation -gt [TimeSpan]::FromSeconds(4)
        bounded = $stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(5)
        targetCleanupSucceeded = $null -eq $targetCleanupErrorType
    }
    return [pscustomobject]@{
        passed = -not ($contractChecks.Values -contains $false) -and
            $null -eq $unexpectedFailureType
        tool = [System.IO.Path]::GetFileName($DiagnosticsTool)
        invocation = @(
            "report",
            "--process-id",
            $targetProcessId)
        elapsedMilliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        ready = $ready
        connected = $connected
        failureKind = $failureKind
        evidence = $evidence
        cleanupFailures = @(
            if ($null -ne $failure) {
                $failure.CleanupFailures
            }
        )
        traceFiles = @($traceFiles | ForEach-Object Name)
        parentRemainingOperationMilliseconds = [Math]::Round(
            $budget.RemainingOperation.TotalMilliseconds,
            3)
        collectorRequestCreatedAtUnixMilliseconds =
            $collectorRequestCreatedAtUnixMilliseconds
        typedOutcomeReturnedAtUnixMilliseconds =
            $typedOutcomeReturnedAtUnixMilliseconds
        unexpectedFailureType = $unexpectedFailureType
        targetCleanupErrorType = $targetCleanupErrorType
        contractChecks = [pscustomobject]$contractChecks
    }
}

function Wait-ForensicsObserverDelay {
    param(
        [ValidateRange(0, 5000)]
        [int]$Milliseconds,
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    if ($Milliseconds -eq 0) {
        return
    }

    $null = $CaptureWindow.DelayAsync(
            [TimeSpan]::FromMilliseconds($Milliseconds),
            $CancellationToken).
        GetAwaiter().GetResult()
}

function Get-DiagnosticProcessTreeSnapshot {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,
        [DateTimeOffset]$NotBeforeUtc = [DateTimeOffset]::MinValue,
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
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

            $remainingMilliseconds = $CaptureWindow.RemainingOperation.TotalMilliseconds
            if ($remainingMilliseconds -lt 1000) {
                throw [TimeoutException]::new(
                    "The diagnostic process snapshot exhausted its owner-allocated capture window.")
            }
            $operationTimeoutSeconds = [Math]::Max(
                1,
                [Math]::Floor([Math]::Min(5, $remainingMilliseconds / 1000)))
            $children = @(
                Get-CimInstance `
                    -ClassName Win32_Process `
                    -Filter "ParentProcessId = $($parent.processId)" `
                    -OperationTimeoutSec $operationTimeoutSeconds `
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

    $collector = (Invoke-OwnedDiagnosticCollector `
        -FileName "ps" `
        -Arguments @("-eo", "pid=,ppid=,comm=") `
        -CaptureWindow $CaptureWindow `
        -CancellationToken $CancellationToken).Evidence
    if ($collector.TimedOut -or $collector.ExitCode -ne 0) {
        throw "Diagnostic process snapshot collector failed."
    }
    $rows = @($collector.StandardOutput -split '\r?\n')
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
        [string]$Destination,
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
        Set-Content -LiteralPath $Destination -Encoding utf8 `
            -Value "dotnet-stack is unavailable. Install it in .tools to capture managed stacks."
        return [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorCleanupFailures = @()
        }
    }

    $collectorFailure = $null
    try {
        $collector = (Invoke-OwnedDiagnosticCollector `
            -FileName $script:diagnosticsTool `
            -Arguments @(
                "report",
                "--process-id",
                $TargetProcessId.ToString(
                    [System.Globalization.CultureInfo]::InvariantCulture)) `
            -CaptureWindow $CaptureWindow `
            -CancellationToken $CancellationToken).Evidence
    }
    catch {
        $collectorFailure = Get-DiagnosticCollectorExecutionFailure `
            -Exception $_.Exception
        if ($null -eq $collectorFailure -or -not (
                Test-DiagnosticCollectorFailureHasCapturedStack `
                    -Exception $collectorFailure)) {
            throw
        }

        $collector = $collectorFailure.Failure.Evidence
    }
    [System.IO.File]::WriteAllText(
        $Destination,
        $collector.StandardOutput + $collector.StandardError,
        [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        available = $true
        captured = if ($null -ne $collectorFailure) {
            $true
        }
        else {
            -not $collector.TimedOut -and
                $collector.ExitCode -eq 0 -and
                -not [string]::IsNullOrWhiteSpace($collector.StandardOutput)
        }
        exitCode = $collector.ExitCode
        timedOut = $collector.TimedOut
        collectorFailureKind = if ($null -eq $collectorFailure) {
            $null
        }
        else {
            $collectorFailure.Failure.Kind.ToString()
        }
        collectorEvidence = if ($null -eq $collectorFailure) {
            $null
        }
        else {
            $collectorFailure.Failure.Evidence
        }
        collectorCleanupFailures = @(
            if ($null -ne $collectorFailure) {
                $collectorFailure.CleanupFailures | ForEach-Object {
                    [pscustomobject]@{
                        kind = $_.Kind.ToString()
                        causeType = $_.Cause.GetType().Name
                    }
                }
            }
        )
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
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None,
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
        Get-DiagnosticProcessTreeSnapshot `
            -RootProcessId $Process.Id `
            -CaptureWindow $CaptureWindow `
            -CancellationToken $CancellationToken)
    $stackResult = if ($Process.HasExited -or $SkipManagedStack) {
        [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorCleanupFailures = @()
        }
    }
    else {
        Save-ManagedStack `
            -TargetProcessId $Process.Id `
            -Destination (Join-Path $directory "managed-stack.txt") `
            -CaptureWindow $CaptureWindow `
            -CancellationToken $CancellationToken
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
    return [pscustomobject]@{
        evidencePath = [System.IO.Path]::GetRelativePath($runRoot, $directory).
            Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        managedStack = $stackResult
    }
}

function Invoke-ForensicsObserverCapture {
    param(
        [Parameter(Mandatory)]
        [int]$TargetProcessId,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Reason,
        [Parameter(Mandatory)]
        [object]$CaptureWindow,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None,
        [ValidateRange(0, 5000)]
        [int]$CaptureDelayMilliseconds = 0,
        [ValidateRange(0, 5000)]
        [int]$InjectedPostCaptureDelayMilliseconds = 0,
        [switch]$InjectFailure,
        [switch]$SkipManagedStack
    )

    if ($InjectFailure) {
        return [pscustomobject]@{
            status = "capture-failed"
            evidencePath = $null
            errorType = "InjectedForensicsObserverFailure"
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorCleanupFailures = @()
        }
    }

    Wait-ForensicsObserverDelay `
        -Milliseconds $CaptureDelayMilliseconds `
        -CaptureWindow $CaptureWindow `
        -CancellationToken $CancellationToken
    $diagnosticProcess = Get-Process `
        -Id $TargetProcessId `
        -ErrorAction SilentlyContinue
    if ($null -eq $diagnosticProcess) {
        return [pscustomobject]@{
            status = "process-exited-before-capture"
            evidencePath = $null
            errorType = $null
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorCleanupFailures = @()
        }
    }

    try {
        $evidenceCapture = Save-ProcessEvidence `
            -Process $diagnosticProcess `
            -AssemblyName $AssemblyName `
            -Iteration $Iteration `
                -Phase $Phase `
                -Reason $Reason `
                -CaptureWindow $CaptureWindow `
                -CancellationToken $CancellationToken `
                -SkipManagedStack:$SkipManagedStack
        if ($InjectedPostCaptureDelayMilliseconds -gt 0) {
            $null = $CaptureWindow.DelayAsync(
                    [TimeSpan]::FromMilliseconds(
                        $InjectedPostCaptureDelayMilliseconds),
                    [Threading.CancellationToken]::None).
                GetAwaiter().GetResult()
        }
        return [pscustomobject]@{
            status = "captured"
            evidencePath = $evidenceCapture.evidencePath
            errorType = if (
                [string]::IsNullOrWhiteSpace(
                    $evidenceCapture.managedStack.collectorFailureKind)) {
                $null
            }
            else {
                "DiagnosticCollectorExecutionException"
            }
            collectorFailureKind =
                $evidenceCapture.managedStack.collectorFailureKind
            collectorEvidence = $evidenceCapture.managedStack.collectorEvidence
            collectorCleanupFailures = @(
                $evidenceCapture.managedStack.collectorCleanupFailures)
        }
    }
    catch {
        $collectorFailure = Get-DiagnosticCollectorExecutionFailure `
            -Exception $_.Exception
        if ($null -ne $collectorFailure) {
            return ConvertTo-DiagnosticCollectorFailureReport `
                -Exception $collectorFailure
        }

        return [pscustomobject]@{
            status = "capture-failed"
            evidencePath = $null
            errorType = $_.Exception.GetType().Name
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorCleanupFailures = @()
        }
    }
    finally {
        $diagnosticProcess.Dispose()
    }
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
        [string]$Phase,
        [Parameter(Mandatory)]
        [object]$Budget,
        [switch]$InjectObserverFailure
    )

    $null = Get-TransitionBudgetWaitMilliseconds -Budget $Budget -Cleanup
    if ($InjectObserverFailure) {
        throw [InvalidOperationException]::new(
            "Injected forensics observer failure.")
    }

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
        [ValidateRange(0, 5000)]
        [int]$InjectedPostCaptureDelayMilliseconds = 0,
        [ValidateRange(-300000, 300000)]
        [int]$InjectedCaptureCompletionUtcOffsetMilliseconds = 0,
        [switch]$InjectCaptureCompletionAfterEvidenceHoldRelease,
        [switch]$SkipSlowEvidenceManagedStack,
        [switch]$InjectForensicsObserverFailure,
        [switch]$AuthorizeRepositoryTestAssembly,
        [ValidateRange(1, 3600)]
        [int]$OperationTimeoutSeconds = $PhaseTimeoutSeconds,
        [double]$EvidenceThresholdSeconds = $SlowPhaseThresholdSeconds,
        [ValidateRange(0, 60000)]
        [int]$EvidenceCaptureLeadMilliseconds =
            $slowEvidenceCaptureLeadMilliseconds,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    if ($InjectCaptureCompletionAfterEvidenceHoldRelease -and
        -not $HoldForEvidenceCapture) {
        throw "Completion-after-release injection requires an evidence hold."
    }

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

    $evidenceHoldRequest = if ($HoldForEvidenceCapture) {
        [DownKyi.ProcessSupervision.EvidenceHoldRequest]::new(
            "DOWNKYI_FORENSICS_CAPTURE_PIPE",
            0xA5,
            0x5A)
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
    $observerCancellation = $null
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
    $slowEvidenceCollectorFailureKind = $null
    $slowEvidenceCollectorEvidence = $null
    $slowEvidenceCollectorCleanupFailures = @()
    $slowEvidenceTriggeredBeforeThreshold = $false
    $slowEvidenceCaptureArmedAfterMilliseconds = $null
    $slowEvidenceCaptureArmedAtUnixMilliseconds = $null
    $slowEvidenceCaptureCompletedAfterMilliseconds = $null
    $slowEvidenceCaptureCompletedAtUnixMilliseconds = $null
    $slowEvidenceCaptureCompletedBeforeTargetExit = $false
    $residualChildEvidenceStatus = "not-triggered"
    $residualChildEvidenceErrorType = $null
    $exitEvidenceStatus = "not-triggered"
    $exitEvidenceErrorType = $null
    $exitEvidenceCollectorFailureKind = $null
    $exitEvidenceCollectorEvidence = $null
    $exitEvidenceCollectorCleanupFailures = @()
    $exitEvidenceCaptured = $false
    $teardownObservedAt = $null
    $evidenceCaptureThresholdSeconds = [Math]::Max(
        0.0,
        $EvidenceThresholdSeconds - ($EvidenceCaptureLeadMilliseconds / 1000))
    try {
        $lease = if ($null -eq $evidenceHoldRequest) {
            [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
                $launchSpec,
                $budget,
                $CancellationToken).GetAwaiter().GetResult()
        }
        else {
            [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
                $launchSpec,
                $budget,
                $evidenceHoldRequest,
                $CancellationToken).GetAwaiter().GetResult()
        }
        if ($null -ne $authorization) {
            $authorization.Item2.ChildProcessId = $lease.TargetProcessId
            Complete-DownKyiTestProcessAuthorization -Authorization $authorization
        }
        $processId = $lease.TargetProcessId
        $observerCancellation = [Threading.CancellationTokenSource]::CreateLinkedTokenSource(
                $CancellationToken,
                $lease.TargetExitedToken)
        $waitTask = $lease.WaitAsync($CancellationToken)
        $evidenceObservationStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $waitTask.IsCompleted) {
            if (-not $slowEvidenceAttempted -and
                $evidenceObservationStopwatch.Elapsed.TotalSeconds -ge
                    $evidenceCaptureThresholdSeconds) {
                $slowEvidenceTriggeredBeforeThreshold =
                    $evidenceObservationStopwatch.Elapsed.TotalSeconds -lt
                        $EvidenceThresholdSeconds
                $slowEvidenceCaptureArmedAfterMilliseconds = [Math]::Round(
                    $evidenceObservationStopwatch.Elapsed.TotalMilliseconds,
                    3)
                $slowEvidenceCaptureArmedAtUnixMilliseconds =
                    [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                $slowEvidenceAttempted = $true
                $captureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $captureWindow = $budget.AllocateDiagnosticCollectorWindow(
                        [TimeSpan]::FromMilliseconds(
                            $forensicsCaptureWindowMilliseconds),
                        [TimeSpan]::FromMilliseconds(
                            $forensicsCaptureCleanupWindowMilliseconds))
                    $capture = Invoke-ForensicsObserverCapture `
                        -TargetProcessId $processId `
                        -AssemblyName $AssemblyName `
                        -Iteration $Iteration `
                        -Phase $Phase `
                        -Reason "slow-phase" `
                        -CaptureWindow $captureWindow `
                        -CancellationToken $observerCancellation.Token `
                        -CaptureDelayMilliseconds $EvidenceCaptureDelayMilliseconds `
                        -InjectedPostCaptureDelayMilliseconds `
                            $InjectedPostCaptureDelayMilliseconds `
                        -SkipManagedStack:$SkipSlowEvidenceManagedStack `
                        -InjectFailure:$InjectForensicsObserverFailure
                    if (-not $InjectCaptureCompletionAfterEvidenceHoldRelease) {
                        $slowEvidenceCaptureCompletedAfterMilliseconds = [Math]::Round(
                            [Math]::Max(
                                0.0,
                                ($OperationTimeoutSeconds * 1000.0) -
                                    $budget.RemainingOperation.TotalMilliseconds),
                            3)
                        $slowEvidenceCaptureCompletedAtUnixMilliseconds =
                            [DateTimeOffset]::UtcNow.AddMilliseconds(
                                $InjectedCaptureCompletionUtcOffsetMilliseconds).
                                ToUnixTimeMilliseconds()
                    }
                    $slowEvidenceStatus = $capture.status
                    $slowEvidenceErrorType = $capture.errorType
                    $slowEvidenceCollectorFailureKind = $capture.collectorFailureKind
                    $slowEvidenceCollectorEvidence = $capture.collectorEvidence
                    $slowEvidenceCollectorCleanupFailures = @(
                        $capture.collectorCleanupFailures)
                    if (-not [string]::IsNullOrWhiteSpace($capture.evidencePath)) {
                        $evidence += $capture.evidencePath
                        $slowEvidence += $capture.evidencePath
                        $slowEvidenceCaptured = $true
                    }
                }
                catch {
                    if (-not $InjectCaptureCompletionAfterEvidenceHoldRelease) {
                        $slowEvidenceCaptureCompletedAfterMilliseconds = [Math]::Round(
                            [Math]::Max(
                                0.0,
                                ($OperationTimeoutSeconds * 1000.0) -
                                    $budget.RemainingOperation.TotalMilliseconds),
                            3)
                        $slowEvidenceCaptureCompletedAtUnixMilliseconds =
                            [DateTimeOffset]::UtcNow.AddMilliseconds(
                                $InjectedCaptureCompletionUtcOffsetMilliseconds).
                                ToUnixTimeMilliseconds()
                    }
                    $slowEvidenceStatus = "capture-failed"
                    $slowEvidenceErrorType = $_.Exception.GetType().Name
                }
                finally {
                    if ($null -ne $evidenceHoldRequest) {
                        try {
                            $completion = if ($slowEvidenceCaptured) {
                                [DownKyi.ProcessSupervision.EvidenceCaptureCompletion]::Captured
                            }
                            else {
                                [DownKyi.ProcessSupervision.EvidenceCaptureCompletion]::Failed
                            }
                            $lease.CompleteEvidenceHoldAsync(
                                    $completion,
                                    $CancellationToken).
                                GetAwaiter().GetResult()
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
                    if ($InjectCaptureCompletionAfterEvidenceHoldRelease) {
                        $targetExitObserved = @(
                            $targetExitWaitHandle = $lease.TargetExitedToken.WaitHandle
                            $targetExitWaitHandle.WaitOne($budget.RemainingOperation)
                        )[-1]
                        if (-not $targetExitObserved) {
                            throw "Completion-after-release injection did not observe target exit."
                        }

                        $slowEvidenceCaptureCompletedAfterMilliseconds = [Math]::Round(
                            [Math]::Max(
                                0.0,
                                ($OperationTimeoutSeconds * 1000.0) -
                                    $budget.RemainingOperation.TotalMilliseconds),
                            3)
                        $slowEvidenceCaptureCompletedAtUnixMilliseconds =
                            [DateTimeOffset]::UtcNow.AddMilliseconds(
                                $InjectedCaptureCompletionUtcOffsetMilliseconds).
                                ToUnixTimeMilliseconds()
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
                    try {
                        $captureWindow = $budget.AllocateDiagnosticCollectorWindow(
                            [TimeSpan]::FromMilliseconds(
                                $forensicsCaptureWindowMilliseconds),
                            [TimeSpan]::FromMilliseconds(
                                $forensicsCaptureCleanupWindowMilliseconds))
                        $capture = Invoke-ForensicsObserverCapture `
                            -TargetProcessId $processId `
                            -AssemblyName $AssemblyName `
                            -Iteration $Iteration `
                            -Phase $Phase `
                            -Reason "slow-exit-after-teardown" `
                            -CaptureWindow $captureWindow `
                            -CancellationToken $observerCancellation.Token `
                            -InjectFailure:$InjectForensicsObserverFailure
                        $exitEvidenceStatus = $capture.status
                        $exitEvidenceErrorType = $capture.errorType
                        $exitEvidenceCollectorFailureKind = $capture.collectorFailureKind
                        $exitEvidenceCollectorEvidence = $capture.collectorEvidence
                        $exitEvidenceCollectorCleanupFailures = @(
                            $capture.collectorCleanupFailures)
                        if (-not [string]::IsNullOrWhiteSpace($capture.evidencePath)) {
                            $evidence += $capture.evidencePath
                            $exitEvidence += $capture.evidencePath
                        }
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

            Start-Sleep -Milliseconds 25
        }

        $stopwatch.Stop()
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
        $targetExitedAfter = if ($null -ne $outcome) {
            $outcome.TargetExitedAfter
        }
        else {
            $ownedFailure.Failure.TargetExitedAfter
        }
        $slowEvidenceCaptureCompletedBeforeTargetExit =
            $null -ne $slowEvidenceCaptureCompletedAfterMilliseconds -and
            $null -ne $targetExitedAfter -and
            $slowEvidenceCaptureCompletedAfterMilliseconds -lt
                $targetExitedAfter.TotalMilliseconds
        $phaseDurationMs = if ($null -ne $targetExitedAfter) {
            $targetExitedAfter.TotalMilliseconds
        }
        else {
            $stopwatch.Elapsed.TotalMilliseconds
        }
        if ($phaseDurationMs -ge ($EvidenceThresholdSeconds * 1000)) {
            $slowThresholdExceeded = $true
            if (-not $slowEvidenceAttempted) {
                $slowEvidenceStatus = "process-exited-before-capture"
            }
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
                    -Phase $Phase `
                    -Budget $budget `
                    -InjectObserverFailure:$InjectForensicsObserverFailure
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
            durationMs = [Math]::Round($phaseDurationMs, 3)
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
            exitEvidenceStatus = $exitEvidenceStatus
            exitEvidenceErrorType = $exitEvidenceErrorType
            exitEvidenceCollectorFailureKind = $exitEvidenceCollectorFailureKind
            exitEvidenceCollectorEvidence = $exitEvidenceCollectorEvidence
            exitEvidenceCollectorCleanupFailures = @(
                $exitEvidenceCollectorCleanupFailures)
            timeoutEvidence = $timeoutEvidence
            diagnosticCaptureDurationMs = [Math]::Round($diagnosticCaptureDurationMs, 3)
            slowThresholdExceeded = $slowThresholdExceeded
            slowEvidenceStatus = $slowEvidenceStatus
            slowEvidenceErrorType = $slowEvidenceErrorType
            slowEvidenceCollectorFailureKind = $slowEvidenceCollectorFailureKind
            slowEvidenceCollectorEvidence = $slowEvidenceCollectorEvidence
            slowEvidenceCollectorCleanupFailures = @(
                $slowEvidenceCollectorCleanupFailures)
            slowEvidenceTriggeredBeforeThreshold =
                $slowEvidenceTriggeredBeforeThreshold
            slowEvidenceCaptureArmedAfterMilliseconds =
                $slowEvidenceCaptureArmedAfterMilliseconds
            slowEvidenceCaptureArmedAtUnixMilliseconds =
                $slowEvidenceCaptureArmedAtUnixMilliseconds
            slowEvidenceCaptureCompletedAfterMilliseconds =
                $slowEvidenceCaptureCompletedAfterMilliseconds
            slowEvidenceCaptureCompletedAtUnixMilliseconds =
                $slowEvidenceCaptureCompletedAtUnixMilliseconds
            slowEvidenceCaptureCompletedBeforeTargetExit =
                $slowEvidenceCaptureCompletedBeforeTargetExit
            evidenceCaptureThresholdSeconds =
                $evidenceCaptureThresholdSeconds
            evidenceCaptureLeadMilliseconds =
                $EvidenceCaptureLeadMilliseconds
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
            evidenceHold = if ($null -ne $outcome) {
                $outcome.EvidenceHold
            }
            else {
                $ownedFailure.Failure.EvidenceHold
            }
            processExitedAtUnixMs = $processExitedAtUnixMs
            targetExitedAfterMilliseconds = if ($null -eq $targetExitedAfter) {
                $null
            }
            else {
                [Math]::Round($targetExitedAfter.TotalMilliseconds, 3)
            }
            observedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
    }
    catch {
        $operationFailure = $_.Exception
    }

    $cleanupFailures = [Collections.Generic.List[Exception]]::new()
    try {
        if ($null -ne $observerCancellation) {
            $observerCancellation.Dispose()
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    try {
        if ($null -ne $lease) {
            $null = $lease.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
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
    $processFailureType = if ($ProcessResult.timedOut) {
        "Timeout"
    }
    elseif (-not $ownedCleanupComplete) {
        "ProcessCleanupFailed"
    }
    elseif (-not $ProcessResult.ownedTreeQuiescent -or
        $ProcessResult.residualChildren.Count -gt 0) {
        "ResidualChildProcess"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ProcessResult.ownedProcessFailureKind)) {
        "ProcessPhaseFailed"
    }
    else {
        $null
    }
    $forensicsFailures = @(
        if (-not $slowEvidenceComplete) {
            "SlowEvidenceMissing"
        }
        if ($ProcessResult.exitEvidenceStatus -eq "capture-failed") {
            "ExitEvidenceCaptureFailed"
        }
        if ($ProcessResult.residualChildEvidenceStatus -eq "capture-failed") {
            "ResidualEvidenceCaptureFailed"
        }
    )
    $forensicsFailureType = if ($forensicsFailures.Count -eq 0) {
        $null
    }
    else {
        $forensicsFailures -join ";"
    }
    $success = $ProcessResult.exitCode -eq 0 -and
        $null -eq $processFailureType -and
        $protocolValid -and
        $stderrClean -and
        $null -eq $forensicsFailureType -and
        $unexpectedText.Count -eq 0
    $failureType = if ($success) {
        $null
    }
    elseif ($null -ne $processFailureType) {
        $processFailureType
    }
    elseif (-not $slowEvidenceComplete) {
        "SlowEvidenceMissing"
    }
    elseif ($null -ne $forensicsFailureType) {
        "ForensicsCaptureFailed"
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
        $ProcessResult.ownedProcessFailureKind
    }
    elseif ($failureType -eq "ProcessCleanupFailed") {
        [string]$ProcessResult.ownedProcessCleanupFailures[0]
    }
    elseif ($failureType -eq "ForensicsCaptureFailed") {
        @(
            $ProcessResult.exitEvidenceErrorType,
            $ProcessResult.residualChildEvidenceErrorType |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        ) -join ";"
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
        processFailureType = $processFailureType
        forensicsFailureType = $forensicsFailureType
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
        exitEvidenceStatus = $ProcessResult.exitEvidenceStatus
        exitEvidenceErrorType = $ProcessResult.exitEvidenceErrorType
        exitEvidenceCollectorFailureKind =
            $ProcessResult.exitEvidenceCollectorFailureKind
        exitEvidenceCollectorEvidence = $ProcessResult.exitEvidenceCollectorEvidence
        exitEvidenceCollectorCleanupFailures = @(
            $ProcessResult.exitEvidenceCollectorCleanupFailures)
        timeoutEvidence = $ProcessResult.timeoutEvidence
        diagnosticCaptureDurationMs = $ProcessResult.diagnosticCaptureDurationMs
        slowThresholdExceeded = $ProcessResult.slowThresholdExceeded
        slowEvidenceStatus = $ProcessResult.slowEvidenceStatus
        slowEvidenceErrorType = $ProcessResult.slowEvidenceErrorType
        slowEvidenceCollectorFailureKind =
            $ProcessResult.slowEvidenceCollectorFailureKind
        slowEvidenceCollectorEvidence = $ProcessResult.slowEvidenceCollectorEvidence
        slowEvidenceCollectorCleanupFailures = @(
            $ProcessResult.slowEvidenceCollectorCleanupFailures)
        slowEvidenceTriggeredBeforeThreshold =
            $ProcessResult.slowEvidenceTriggeredBeforeThreshold
        slowEvidenceCaptureArmedAfterMilliseconds =
            $ProcessResult.slowEvidenceCaptureArmedAfterMilliseconds
        slowEvidenceCaptureArmedAtUnixMilliseconds =
            $ProcessResult.slowEvidenceCaptureArmedAtUnixMilliseconds
        slowEvidenceCaptureCompletedAfterMilliseconds =
            $ProcessResult.slowEvidenceCaptureCompletedAfterMilliseconds
        slowEvidenceCaptureCompletedAtUnixMilliseconds =
            $ProcessResult.slowEvidenceCaptureCompletedAtUnixMilliseconds
        slowEvidenceCaptureCompletedBeforeTargetExit =
            $ProcessResult.slowEvidenceCaptureCompletedBeforeTargetExit
        evidenceCaptureThresholdSeconds =
            $ProcessResult.evidenceCaptureThresholdSeconds
        evidenceCaptureLeadMilliseconds =
            $ProcessResult.evidenceCaptureLeadMilliseconds
        ownedTreeQuiescent = $ProcessResult.ownedTreeQuiescent
        ownedProcessFailureKind = $ProcessResult.ownedProcessFailureKind
        ownedProcessCleanupFailures = @($ProcessResult.ownedProcessCleanupFailures)
        processOwnership = $ProcessResult.processOwnership
        evidenceHold = $ProcessResult.evidenceHold
        processExitedAtUnixMs = $ProcessResult.processExitedAtUnixMs
        targetExitedAfterMilliseconds =
            $ProcessResult.targetExitedAfterMilliseconds
    }
}

function Test-SlowEvidenceCaptureOrdering {
    param(
        [Parameter(Mandatory)]
        [string]$ProcessSupervisionAssembly
    )

    $targetDelayMilliseconds = 8000
    $immediateDispatchTargetDelayMilliseconds = 1000
    $slowCompletionTargetDelayMilliseconds = 5250
    $collectorDelayMilliseconds = 5000
    $slowCompletionDelayMilliseconds = 4000
    $slowCompletionUtcOffsetMilliseconds = -60000
    $evidenceThresholdSeconds = 5
    $fixtureDirectory = Join-Path $rawRoot "Gate.SlowEvidenceOrdering"
    $configuredReadyPath = Join-Path $fixtureDirectory "configured-ready.json"
    $mutationReadyPath = Join-Path $fixtureDirectory "one-second-ready.json"
    $immediateDispatchReadyPath = Join-Path `
        $fixtureDirectory `
        "immediate-dispatch-ready.json"
    $slowCompletionReadyPath = Join-Path `
        $fixtureDirectory `
        "slow-completion-ready.json"
    New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
    $configured = $null
    $configuredPhase = $null
    $configuredReady = $null
    $mutation = $null
    $mutationPhase = $null
    $mutationReady = $null
    $immediateDispatchMutation = $null
    $immediateDispatchMutationPhase = $null
    $immediateDispatchReady = $null
    $slowCompletionMutation = $null
    $slowCompletionMutationPhase = $null
    $slowCompletionReady = $null
    $readyCleanupFailures = [Collections.Generic.List[string]]::new()
    $errorType = $null
    try {
        $configured = Invoke-IsolatedProcess `
            -AssemblyName "Gate.SlowEvidenceOrdering.Configured" `
            -Iteration 1 `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @(
                $ProcessSupervisionAssembly,
                "--exit-after-delay-with-ready",
                $configuredReadyPath,
                [string]$targetDelayMilliseconds
            ) `
            -EvidenceCaptureDelayMilliseconds $collectorDelayMilliseconds `
            -SkipSlowEvidenceManagedStack `
            -OperationTimeoutSeconds 15 `
            -EvidenceThresholdSeconds $evidenceThresholdSeconds `
            -EvidenceCaptureLeadMilliseconds `
                $hostedCollectorStartupAllowanceMilliseconds
        $configuredPhase = New-ProcessPhaseResult -ProcessResult $configured
        if (Test-Path -LiteralPath $configuredReadyPath -PathType Leaf) {
            $configuredReady = Get-Content -LiteralPath $configuredReadyPath -Raw |
                ConvertFrom-Json
        }

        $mutation = Invoke-IsolatedProcess `
            -AssemblyName "Gate.SlowEvidenceOrdering.OneSecondMutation" `
            -Iteration 1 `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @(
                $ProcessSupervisionAssembly,
                "--exit-after-delay-with-ready",
                $mutationReadyPath,
                [string]$targetDelayMilliseconds
            ) `
            -EvidenceCaptureDelayMilliseconds $collectorDelayMilliseconds `
            -SkipSlowEvidenceManagedStack `
            -OperationTimeoutSeconds 15 `
            -EvidenceThresholdSeconds $evidenceThresholdSeconds `
            -EvidenceCaptureLeadMilliseconds 1000
        $mutationPhase = New-ProcessPhaseResult -ProcessResult $mutation
        if (Test-Path -LiteralPath $mutationReadyPath -PathType Leaf) {
            $mutationReady = Get-Content -LiteralPath $mutationReadyPath -Raw |
                ConvertFrom-Json
        }

        $immediateDispatchMutation = Invoke-IsolatedProcess `
            -AssemblyName "Gate.SlowEvidenceOrdering.ImmediateDispatchMutation" `
            -Iteration 1 `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @(
                $ProcessSupervisionAssembly,
                "--exit-after-delay-with-ready",
                $immediateDispatchReadyPath,
                [string]$immediateDispatchTargetDelayMilliseconds
            ) `
            -InjectForensicsObserverFailure `
            -OperationTimeoutSeconds 10 `
            -EvidenceThresholdSeconds $evidenceThresholdSeconds `
            -EvidenceCaptureLeadMilliseconds 5000
        $immediateDispatchMutationPhase = New-ProcessPhaseResult `
            -ProcessResult $immediateDispatchMutation
        if (Test-Path -LiteralPath $immediateDispatchReadyPath -PathType Leaf) {
            $immediateDispatchReady =
                Get-Content -LiteralPath $immediateDispatchReadyPath -Raw |
                    ConvertFrom-Json
        }

        $slowCompletionMutation = Invoke-IsolatedProcess `
            -AssemblyName "Gate.SlowEvidenceOrdering.SlowCompletionMutation" `
            -Iteration 1 `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @(
                $ProcessSupervisionAssembly,
                "--exit-after-delay-with-ready",
                $slowCompletionReadyPath,
                [string]$slowCompletionTargetDelayMilliseconds
            ) `
            -InjectedPostCaptureDelayMilliseconds `
                $slowCompletionDelayMilliseconds `
            -InjectedCaptureCompletionUtcOffsetMilliseconds `
                $slowCompletionUtcOffsetMilliseconds `
            -SkipSlowEvidenceManagedStack `
            -OperationTimeoutSeconds 12 `
            -EvidenceThresholdSeconds $evidenceThresholdSeconds `
            -EvidenceCaptureLeadMilliseconds `
                $hostedCollectorStartupAllowanceMilliseconds
        $slowCompletionMutationPhase = New-ProcessPhaseResult `
            -ProcessResult $slowCompletionMutation
        if (Test-Path -LiteralPath $slowCompletionReadyPath -PathType Leaf) {
            $slowCompletionReady =
                Get-Content -LiteralPath $slowCompletionReadyPath -Raw |
                    ConvertFrom-Json
        }
    }
    catch {
        $errorType = $_.Exception.GetType().Name
    }
    finally {
        foreach ($readyPath in @(
            $configuredReadyPath,
            $mutationReadyPath,
            $immediateDispatchReadyPath,
            $slowCompletionReadyPath)) {
            if (-not (Test-Path -LiteralPath $readyPath)) {
                continue
            }

            try {
                Remove-Item -LiteralPath $readyPath -Force -ErrorAction Stop
            }
            catch {
                $readyCleanupFailures.Add(
                    "{0}:{1}" -f
                        [System.IO.Path]::GetFileName($readyPath),
                        $_.Exception.GetType().Name)
            }
        }
    }

    $remainingReadyFiles = @(
        foreach ($readyPath in @(
            $configuredReadyPath,
            $mutationReadyPath,
            $immediateDispatchReadyPath,
            $slowCompletionReadyPath)) {
            if (Test-Path -LiteralPath $readyPath) {
                [System.IO.Path]::GetFileName($readyPath)
            }
        }
    )

    $contractChecks = [ordered]@{
        configuredTargetReady = $null -ne $configuredReady -and
            $configuredReady.ProcessId -eq $configured.processId -and
            $configuredReady.DelayMilliseconds -eq $targetDelayMilliseconds -and
            $configuredReady.DelayScheduled -eq $true
        mutationTargetReady = $null -ne $mutationReady -and
            $mutationReady.ProcessId -eq $mutation.processId -and
            $mutationReady.DelayMilliseconds -eq $targetDelayMilliseconds -and
            $mutationReady.DelayScheduled -eq $true
        configuredUsedRealLifecyclePath = $null -ne $configuredPhase -and
            $configuredPhase.success -and
            $configured.slowThresholdExceeded -and
            $configured.slowEvidenceStatus -eq "captured" -and
            $configured.slowEvidence.Count -gt 0
        configuredCaptureArmedAtTwoSeconds = $null -ne $configured -and
            $configured.evidenceCaptureThresholdSeconds -eq 2 -and
            $configured.evidenceCaptureLeadMilliseconds -eq 3000 -and
            $configured.slowEvidenceTriggeredBeforeThreshold -and
            $configured.slowEvidenceCaptureArmedAfterMilliseconds -ge 2000 -and
            $configured.slowEvidenceCaptureArmedAfterMilliseconds -lt
                ($evidenceThresholdSeconds * 1000)
        configuredDelayCompleted = $null -ne $configured -and
            $configured.diagnosticCaptureDurationMs -ge
                ($collectorDelayMilliseconds - 100)
        configuredCaptureCompletedBeforeTargetExit =
            $null -ne $configured -and
            $configured.slowEvidenceCaptureCompletedBeforeTargetExit -and
            $configured.slowEvidenceCaptureCompletedAfterMilliseconds -lt
                $configured.targetExitedAfterMilliseconds
        mutationUsedRealLifecyclePath = $null -ne $mutationPhase -and
            -not $mutationPhase.success -and
            $mutationPhase.failureType -eq "SlowEvidenceMissing" -and
            $mutationPhase.processFailureType -eq $null -and
            $mutation.slowThresholdExceeded -and
            $mutation.slowEvidenceStatus -eq "capture-failed" -and
            $mutation.slowEvidence.Count -eq 0
        mutationCaptureArmedAtFourSeconds = $null -ne $mutation -and
            $mutation.evidenceCaptureThresholdSeconds -eq 4 -and
            $mutation.evidenceCaptureLeadMilliseconds -eq 1000 -and
            $mutation.slowEvidenceTriggeredBeforeThreshold -and
            $mutation.slowEvidenceCaptureArmedAfterMilliseconds -ge 4000 -and
            $mutation.slowEvidenceCaptureArmedAfterMilliseconds -lt
                ($evidenceThresholdSeconds * 1000)
        immediateDispatchMutationDetected =
            $null -ne $immediateDispatchMutation -and
            $null -ne $immediateDispatchMutationPhase -and
            $immediateDispatchMutationPhase.success -and
            $immediateDispatchMutation.evidenceCaptureThresholdSeconds -eq 0 -and
            $immediateDispatchMutation.evidenceCaptureLeadMilliseconds -eq 5000 -and
            $immediateDispatchMutation.slowEvidenceCaptureArmedAfterMilliseconds -ge 0 -and
            $immediateDispatchMutation.slowEvidenceCaptureArmedAfterMilliseconds -lt 500
        slowCompletionMutationDetected =
            $null -ne $slowCompletionMutation -and
            $null -ne $slowCompletionMutationPhase -and
            $slowCompletionMutationPhase.success -and
            $slowCompletionMutation.slowThresholdExceeded -and
            $slowCompletionMutation.slowEvidenceStatus -eq "captured" -and
            $slowCompletionMutation.slowEvidence.Count -gt 0 -and
            -not $slowCompletionMutation.slowEvidenceCaptureCompletedBeforeTargetExit -and
            $slowCompletionMutation.slowEvidenceCaptureCompletedAfterMilliseconds -ge
                $slowCompletionMutation.targetExitedAfterMilliseconds
        slowCompletionWallClockOrderingDiverged =
            $null -ne $slowCompletionMutation -and
            $slowCompletionMutation.slowEvidenceCaptureCompletedAtUnixMilliseconds -lt
                $slowCompletionMutation.processExitedAtUnixMs -and
            $slowCompletionMutation.slowEvidenceCaptureCompletedAfterMilliseconds -ge
                $slowCompletionMutation.targetExitedAfterMilliseconds
        mutationTargetsReady =
            $null -ne $immediateDispatchReady -and
            $immediateDispatchReady.ProcessId -eq
                $immediateDispatchMutation.processId -and
            $immediateDispatchReady.DelayMilliseconds -eq
                $immediateDispatchTargetDelayMilliseconds -and
            $immediateDispatchReady.DelayScheduled -eq $true -and
            $null -ne $slowCompletionReady -and
            $slowCompletionReady.ProcessId -eq $slowCompletionMutation.processId -and
            $slowCompletionReady.DelayMilliseconds -eq
                $slowCompletionTargetDelayMilliseconds -and
            $slowCompletionReady.DelayScheduled -eq $true
        targetOwnershipPreserved = $null -ne $configured -and
            $configured.ownedTreeQuiescent -and
            $configured.ownedProcessCleanupFailures.Count -eq 0 -and
            $null -ne $mutation -and
            $mutation.ownedTreeQuiescent -and
            $mutation.ownedProcessCleanupFailures.Count -eq 0 -and
            $null -ne $immediateDispatchMutation -and
            $immediateDispatchMutation.ownedTreeQuiescent -and
            $immediateDispatchMutation.ownedProcessCleanupFailures.Count -eq 0 -and
            $null -ne $slowCompletionMutation -and
            $slowCompletionMutation.ownedTreeQuiescent -and
            $slowCompletionMutation.ownedProcessCleanupFailures.Count -eq 0
        readyFilesCleaned = $readyCleanupFailures.Count -eq 0 -and
            $remainingReadyFiles.Count -eq 0
        noUnexpectedFailure = $null -eq $errorType
    }
    $passed = -not @($contractChecks.Values | Where-Object { -not $_ }).Count
    return [pscustomobject]@{
        passed = $passed
        targetDelayMilliseconds = $targetDelayMilliseconds
        immediateDispatchTargetDelayMilliseconds =
            $immediateDispatchTargetDelayMilliseconds
        slowCompletionTargetDelayMilliseconds =
            $slowCompletionTargetDelayMilliseconds
        collectorDelayMilliseconds = $collectorDelayMilliseconds
        slowCompletionDelayMilliseconds = $slowCompletionDelayMilliseconds
        slowCompletionUtcOffsetMilliseconds = $slowCompletionUtcOffsetMilliseconds
        evidenceThresholdSeconds = $evidenceThresholdSeconds
        configured = $configured
        configuredPhase = $configuredPhase
        configuredReady = $configuredReady
        mutation = $mutation
        mutationPhase = $mutationPhase
        mutationReady = $mutationReady
        immediateDispatchMutation = $immediateDispatchMutation
        immediateDispatchMutationPhase = $immediateDispatchMutationPhase
        immediateDispatchReady = $immediateDispatchReady
        slowCompletionMutation = $slowCompletionMutation
        slowCompletionMutationPhase = $slowCompletionMutationPhase
        slowCompletionReady = $slowCompletionReady
        readyCleanupFailures = @($readyCleanupFailures)
        remainingReadyFiles = $remainingReadyFiles
        errorType = $errorType
        contractChecks = [pscustomobject]$contractChecks
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
if ($ValidateForensics) {
    $forensicsCollectorCleanupReportSelfTest =
        Test-DiagnosticCollectorCleanupFailureReport
    $forensicsCollectorCleanupReportSelfTestPassed =
        $forensicsCollectorCleanupReportSelfTest.passed
    if (-not $forensicsCollectorCleanupReportSelfTestPassed) {
        throw "Forensics collector cleanup-report self-test did not preserve evidence."
    }
    $forensicsCollectorInterruptedStackSelfTest =
        Test-DiagnosticCollectorInterruptedStackPolicy
    $forensicsCollectorInterruptedStackSelfTestPassed =
        $forensicsCollectorInterruptedStackSelfTest.passed
    $forensicsCollectorInterruptedStackSelfTest |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (
            Join-Path $runRoot "forensics-collector-interrupted-stack-self-test.json") `
            -Encoding utf8
    if (-not $forensicsCollectorInterruptedStackSelfTestPassed) {
        throw "Forensics collector interrupted-stack self-test did not fail closed."
    }
    $forensicsCollectorCaptureWindowSelfTest =
        Test-OwnedDiagnosticCollectorCaptureWindow `
        -ProcessSupervisionAssembly $processSupervisionAssembly
    $forensicsCollectorCaptureWindowSelfTestPassed =
        $forensicsCollectorCaptureWindowSelfTest.passed
    $forensicsCollectorCaptureWindowSelfTest |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $runRoot "forensics-collector-capture-window-self-test.json") `
            -Encoding utf8
    if (-not $forensicsCollectorCaptureWindowSelfTestPassed) {
        Write-Host (
            "Forensics collector capture-window self-test evidence: {0}" -f
            ($forensicsCollectorCaptureWindowSelfTest |
                ConvertTo-Json -Depth 8 -Compress))
        throw "Forensics collector capture-window self-test did not fail closed."
    }
    $slowEvidenceOrderingSelfTest = Test-SlowEvidenceCaptureOrdering `
        -ProcessSupervisionAssembly $processSupervisionAssembly
    $slowEvidenceOrderingSelfTestPassed = $slowEvidenceOrderingSelfTest.passed
    $slowEvidenceOrderingSelfTest |
        ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath (
            Join-Path $runRoot "slow-evidence-ordering-self-test.json") `
            -Encoding utf8
    if (-not $slowEvidenceOrderingSelfTestPassed) {
        Write-Host (
            "Slow-evidence ordering self-test evidence: {0}" -f
            ($slowEvidenceOrderingSelfTest |
                ConvertTo-Json -Depth 12 -Compress))
        throw "Slow-evidence ordering self-test did not reject the one-second lead."
    }
    if ($dotnetStackAttachStallSelfTestRequired) {
        if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
            throw "Forensics validation requires dotnet-stack."
        }
        $dotnetStackAttachStallSelfTest = Test-DotnetStackAttachStall `
            -ProcessSupervisionAssembly $processSupervisionAssembly `
            -DiagnosticsTool $script:diagnosticsTool
        $dotnetStackAttachStallSelfTestPassed =
            $dotnetStackAttachStallSelfTest.passed
        $dotnetStackAttachStallSelfTest |
            ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath (
                Join-Path $runRoot "dotnet-stack-attach-stall-self-test.json") `
                -Encoding utf8
        if (-not $dotnetStackAttachStallSelfTestPassed) {
            Write-Host (
                "dotnet-stack attach-stall self-test evidence: {0}" -f
                ($dotnetStackAttachStallSelfTest |
                    ConvertTo-Json -Depth 12 -Compress))
            throw "dotnet-stack attach-stall self-test did not preserve transition evidence."
        }
    }
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
            $selfTestAssembly
        ) `
        -LifecycleMarkerPath $selfTestMarker `
        -HoldForEvidenceCapture `
        -EvidenceCaptureDelayMilliseconds $forensicsSelfTestCaptureDelayMilliseconds `
        -EvidenceThresholdSeconds $forensicsSelfTestEvidenceThresholdSeconds
    $selfTestPhase = New-ProcessPhaseResult -ProcessResult $selfTest
    $evidenceReports = @(
        foreach ($relativeEvidence in $selfTest.evidence) {
            $evidencePath = Join-Path $runRoot $relativeEvidence "process-evidence.json"
            if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
                Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
            }
        }
    )
    $releaseOrderingMutationOutput = @(
        Invoke-IsolatedProcess `
            -AssemblyName "Gate.Forensics.ReleaseOrderingMutation" `
            -Iteration 1 `
            -Phase "execution" `
            -FileName "dotnet" `
            -Arguments @(
                $probeAssembly,
                "--assembly",
                $selfTestAssembly
            ) `
            -HoldForEvidenceCapture `
            -InjectCaptureCompletionAfterEvidenceHoldRelease `
            -SkipSlowEvidenceManagedStack `
            -EvidenceThresholdSeconds $forensicsSelfTestEvidenceThresholdSeconds
    )
    $releaseOrderingMutationResults = @(
        $releaseOrderingMutationOutput |
            Where-Object { $null -ne $_.PSObject.Properties["assembly"] }
    )
    if ($releaseOrderingMutationResults.Count -ne 1) {
        throw "Release-ordering mutation did not return one typed process result."
    }
    $forensicsSelfTestReleaseOrderingMutation = $releaseOrderingMutationResults[0]
    $releaseOrderingMutationPhase = New-ProcessPhaseResult `
        -ProcessResult $forensicsSelfTestReleaseOrderingMutation
    $releaseOrderingMutation = $forensicsSelfTestReleaseOrderingMutation
    $forensicsSelfTestReleaseOrderingMutationValidated =
        $releaseOrderingMutationPhase.success -and
        $releaseOrderingMutation.slowEvidenceStatus -eq "captured" -and
        $releaseOrderingMutation.evidence.Count -gt 0 -and
        -not $releaseOrderingMutation.slowEvidenceCaptureCompletedBeforeTargetExit -and
        $releaseOrderingMutation.slowEvidenceCaptureCompletedAfterMilliseconds -ge
            $releaseOrderingMutation.targetExitedAfterMilliseconds -and
        $releaseOrderingMutation.evidenceHold.Requested -and
        $releaseOrderingMutation.evidenceHold.Granted -and
        $releaseOrderingMutation.evidenceHold.CaptureCompletion.ToString() -eq
            "Captured" -and
        $releaseOrderingMutation.evidenceHold.Released -and
        $releaseOrderingMutation.evidenceHold.CompletionSignalDelivered -and
        $releaseOrderingMutation.evidenceHold.TargetAcknowledged -and
        $releaseOrderingMutation.ownedTreeQuiescent -and
        $releaseOrderingMutation.ownedProcessCleanupFailures.Count -eq 0
    $forensicsSelfTestObservedCaptureThresholdSeconds = if (
        $null -eq $selfTest.slowEvidenceCaptureArmedAfterMilliseconds) {
        $null
    }
    else {
        [Math]::Round(
            $selfTest.slowEvidenceCaptureArmedAfterMilliseconds / 1000,
            3)
    }
    $forensicsSelfTestPositiveCaptureThresholdValidated =
        $null -ne $forensicsSelfTestObservedCaptureThresholdSeconds -and
        $forensicsSelfTestObservedCaptureThresholdSeconds -ge
            $forensicsSelfTestPositiveCaptureThresholdSeconds -and
        $forensicsSelfTestObservedCaptureThresholdSeconds -lt
            $forensicsSelfTestEvidenceThresholdSeconds
    $forensicsSelfTestCaptureCompletedBeforeTargetExitValidated =
        $selfTest.slowEvidenceCaptureCompletedBeforeTargetExit -and
        $selfTest.slowEvidenceCaptureCompletedAfterMilliseconds -lt
            $selfTest.targetExitedAfterMilliseconds
    $forensicsValid = $selfTestPhase.success -and
        $evidenceReports.Count -gt 0 -and
        @($evidenceReports | Where-Object { $_.managedStack.captured -eq $true }).Count -gt 0 -and
        $selfTest.slowEvidenceTriggeredBeforeThreshold -and
        [Math]::Abs(
            $selfTest.evidenceCaptureThresholdSeconds -
            $forensicsSelfTestPositiveCaptureThresholdSeconds) -le 0.001 -and
        $forensicsSelfTestPositiveCaptureThresholdValidated -and
        $forensicsSelfTestCaptureCompletedBeforeTargetExitValidated -and
        $forensicsSelfTestReleaseOrderingMutationValidated -and
        $selfTest.evidenceHold.Requested -and
        $selfTest.evidenceHold.Granted -and
        $selfTest.evidenceHold.CaptureCompletion.ToString() -eq "Captured" -and
        $selfTest.evidenceHold.Released -and
        $selfTest.evidenceHold.CompletionSignalDelivered -and
        $selfTest.evidenceHold.TargetAcknowledged
    $forensicsCollectorCaptureWindowSelfTestPassed =
        $forensicsCollectorCaptureWindowSelfTestPassed -and $forensicsValid
    $forensicsSelfTestCaptureLeadValidated =
        $selfTest.slowEvidenceTriggeredBeforeThreshold -and
        $forensicsSelfTestPositiveCaptureThresholdValidated -and
        $forensicsSelfTestCaptureCompletedBeforeTargetExitValidated
    $forensicsSelfTestEvidenceHoldValidated =
        $selfTest.evidenceHold.Requested -and
        $selfTest.evidenceHold.Granted -and
        $selfTest.evidenceHold.CaptureCompletion.ToString() -eq "Captured" -and
        $selfTest.evidenceHold.Released -and
        $selfTest.evidenceHold.CompletionSignalDelivered -and
        $selfTest.evidenceHold.TargetAcknowledged
    $phaseResults += [pscustomobject]@{
        assembly = "Gate.Forensics"
        iteration = 1
        phase = "forensics-self-test"
        processId = $selfTest.processId
        success = $forensicsValid
        failureType = if ($forensicsValid) { $null } else { "ForensicsSelfTestFailed" }
        errorType = $selfTestPhase.errorType
        processFailureType = $selfTestPhase.processFailureType
        forensicsFailureType = $selfTestPhase.forensicsFailureType
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
        exitEvidenceStatus = $selfTest.exitEvidenceStatus
        exitEvidenceErrorType = $selfTest.exitEvidenceErrorType
        timeoutEvidence = $selfTest.timeoutEvidence
        diagnosticCaptureDurationMs = $selfTest.diagnosticCaptureDurationMs
        slowThresholdExceeded = $false
        slowEvidenceStatus = "not-applicable"
        slowEvidenceErrorType = $null
        slowEvidenceTriggeredBeforeThreshold =
            $selfTest.slowEvidenceTriggeredBeforeThreshold
        slowEvidenceCaptureArmedAfterMilliseconds =
            $selfTest.slowEvidenceCaptureArmedAfterMilliseconds
        slowEvidenceCaptureArmedAtUnixMilliseconds =
            $selfTest.slowEvidenceCaptureArmedAtUnixMilliseconds
        slowEvidenceCaptureCompletedAfterMilliseconds =
            $selfTest.slowEvidenceCaptureCompletedAfterMilliseconds
        slowEvidenceCaptureCompletedAtUnixMilliseconds =
            $selfTest.slowEvidenceCaptureCompletedAtUnixMilliseconds
        slowEvidenceCaptureCompletedBeforeTargetExit =
            $selfTest.slowEvidenceCaptureCompletedBeforeTargetExit
        evidenceCaptureThresholdSeconds =
            $selfTest.evidenceCaptureThresholdSeconds
        evidenceCaptureLeadMilliseconds =
            $selfTest.evidenceCaptureLeadMilliseconds
        ownedTreeQuiescent = $selfTest.ownedTreeQuiescent
        ownedProcessFailureKind = $selfTest.ownedProcessFailureKind
        ownedProcessCleanupFailures = @($selfTest.ownedProcessCleanupFailures)
        processOwnership = $selfTest.processOwnership
        evidenceHold = $selfTest.evidenceHold
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
            -OperationTimeoutSeconds 3 `
            -EvidenceThresholdSeconds 60 `
            -InjectForensicsObserverFailure
        $processLeaseProbePhase = New-ProcessPhaseResult `
            -ProcessResult $processLeaseProbe
        $processLeaseSelfTest.parentExited =
            Test-Path -LiteralPath $processLeaseReadyPath -PathType Leaf
        $processLeaseSelfTest.ownedTreeRejected =
            -not $processLeaseProbePhase.success -and
            $processLeaseProbePhase.failureType -eq "ResidualChildProcess" -and
            $processLeaseProbe.ownedProcessFailureKind -eq "OwnedTreeNotQuiescent"
        $processLeaseSelfTest.diagnosticObserverMissedDescendant =
            $processLeaseProbe.observedChildren.Count -eq 0 -and
            $processLeaseProbePhase.failureType -eq "ResidualChildProcess"
        $processLeaseSelfTest.observerFailurePreserved =
            $processLeaseProbePhase.processFailureType -eq "ResidualChildProcess" -and
            $processLeaseProbePhase.forensicsFailureType.Contains(
                "ResidualEvidenceCaptureFailed",
                [StringComparison]::Ordinal) -and
            $processLeaseProbe.residualChildEvidenceStatus -eq "capture-failed" -and
            -not [string]::IsNullOrWhiteSpace(
                $processLeaseProbe.residualChildEvidenceErrorType)
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
        $processLeaseSelfTest.diagnosticObserverMissedDescendant -and
        $processLeaseSelfTest.observerFailurePreserved -and
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
    schemaVersion = 4
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
    forensicsSelfTestPositiveCaptureThresholdSeconds =
        $forensicsSelfTestPositiveCaptureThresholdSeconds
    forensicsSelfTestPositiveCaptureThresholdValidated =
        $forensicsSelfTestPositiveCaptureThresholdValidated
    forensicsSelfTestObservedCaptureThresholdSeconds =
        $forensicsSelfTestObservedCaptureThresholdSeconds
    forensicsSelfTestCaptureCompletedBeforeTargetExitValidated =
        $forensicsSelfTestCaptureCompletedBeforeTargetExitValidated
    forensicsSelfTestEvidenceThresholdSeconds =
        $forensicsSelfTestEvidenceThresholdSeconds
    forensicsCaptureWindowMilliseconds = $forensicsCaptureWindowMilliseconds
    forensicsCaptureCleanupWindowMilliseconds =
        $forensicsCaptureCleanupWindowMilliseconds
    processCleanupGraceSeconds = $processCleanupGraceSeconds
    forensicsSelfTestCaptureLeadValidated =
        $forensicsSelfTestCaptureLeadValidated
    forensicsSelfTestEvidenceHoldValidated =
        $forensicsSelfTestEvidenceHoldValidated
    forensicsSelfTestReleaseOrderingMutationValidated =
        $forensicsSelfTestReleaseOrderingMutationValidated
    forensicsSelfTestReleaseOrderingMutation =
        $forensicsSelfTestReleaseOrderingMutation
    slowEvidenceOrderingSelfTestRequired = [bool]$ValidateForensics
    slowEvidenceOrderingSelfTestPassed = if ($ValidateForensics) {
        $slowEvidenceOrderingSelfTestPassed
    }
    else {
        $null
    }
    slowEvidenceOrderingSelfTest = $slowEvidenceOrderingSelfTest
    reporterContractSelfTestPassed = $reporterContractSelfTestPassed
    forensicsCollectorCaptureWindowSelfTestRequired = [bool]$ValidateForensics
    forensicsCollectorCaptureWindowSelfTestPassed =
        $forensicsCollectorCaptureWindowSelfTestPassed
    forensicsCollectorCaptureWindowSelfTest =
        $forensicsCollectorCaptureWindowSelfTest
    forensicsCollectorCleanupReportSelfTestRequired = [bool]$ValidateForensics
    forensicsCollectorCleanupReportSelfTestPassed =
        $forensicsCollectorCleanupReportSelfTestPassed
    forensicsCollectorCleanupReportSelfTest =
        $forensicsCollectorCleanupReportSelfTest
    forensicsCollectorInterruptedStackSelfTestRequired = [bool]$ValidateForensics
    forensicsCollectorInterruptedStackSelfTestPassed =
        $forensicsCollectorInterruptedStackSelfTestPassed
    forensicsCollectorInterruptedStackSelfTest =
        $forensicsCollectorInterruptedStackSelfTest
    dotnetStackAttachStallSelfTestRequired =
        $dotnetStackAttachStallSelfTestRequired
    dotnetStackAttachStallSelfTestPassed = if (
        $dotnetStackAttachStallSelfTestRequired) {
        $dotnetStackAttachStallSelfTestPassed
    }
    else {
        $null
    }
    dotnetStackAttachStallSelfTest = $dotnetStackAttachStallSelfTest
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
    "$forensicsSelfTestCaptureLeadValidated; " +
    "positive threshold=" +
    "$forensicsSelfTestPositiveCaptureThresholdSeconds s")
$markdown.Add(
    "- Slow-evidence ordering self-test: required=$([bool]$ValidateForensics), " +
    "passed=$slowEvidenceOrderingSelfTestPassed")
$markdown.Add(
    "- Supervisor-owned evidence-hold self-test: " +
    "$forensicsSelfTestEvidenceHoldValidated")
$markdown.Add(
    "- dotnet-stack attach-stall self-test: required=" +
    "$dotnetStackAttachStallSelfTestRequired, " +
    "passed=$dotnetStackAttachStallSelfTestPassed")
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
