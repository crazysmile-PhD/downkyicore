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
    [switch]$NoBuild,
    [switch]$ValidateTargetExitDuringCapture
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
$processCleanupGraceSeconds = 5
$forensicsCaptureWindowMilliseconds = 15000
$forensicsCaptureCleanupWindowMilliseconds = $processCleanupGraceSeconds * 1000

New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

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
        [Threading.CancellationToken]$StartupCancellationToken =
            [Threading.CancellationToken]::None,
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
            $StartupCancellationToken,
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

function Test-DiagnosticCollectorFailureHasCapturedStack {
    param(
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException]$Exception
    )

    $evidence = $Exception.Failure.Evidence
    $stackOutputTransition = @(
        $evidence.Timeline.Transitions |
            Where-Object {
                $_.Transition -eq
                    [DownKyi.ProcessSupervision.DiagnosticCollectorTransition]::StackOutputFirstByte
            }
    ) | Select-Object -First 1
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

function ConvertTo-ExceptionEvidence {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    $innerFailures = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[object]]::new()
    $pending.Enqueue([pscustomobject]@{
        exception = $Exception
        path = "outer"
    })
    while ($pending.Count -gt 0) {
        $entry = $pending.Dequeue()
        $candidate = $entry.exception
        if ($entry.path -ne "outer") {
            $innerFailures.Add([pscustomobject]@{
                path = $entry.path
                type = $candidate.GetType().FullName
                message = $candidate.Message
                stack = $candidate.StackTrace
            })
        }

        $children = @(
            if ($candidate -is [AggregateException]) {
                $candidate.InnerExceptions
            }
            elseif ($null -ne $candidate.InnerException) {
                $candidate.InnerException
            }
        )
        if ($children.Count -gt 0) {
            for ($index = 0; $index -lt $children.Count; $index++) {
                $pending.Enqueue([pscustomobject]@{
                    exception = $children[$index]
                    path = ("{0}.inner[{1}]" -f $entry.path, $index)
                })
            }
        }
    }

    $primaryException = if (
        $Exception.Data.Contains("DownKyi.Lifecycle.PrimaryFailure")) {
        $Exception.Data["DownKyi.Lifecycle.PrimaryFailure"]
    }
    else {
        $Exception
    }
    $cleanupExceptions = @(
        if ($Exception.Data.Contains("DownKyi.Lifecycle.CleanupFailures")) {
            $Exception.Data["DownKyi.Lifecycle.CleanupFailures"]
        }
    )
    $firstCausalException = if ($null -ne $primaryException) {
        $primaryException
    }
    elseif ($cleanupExceptions.Count -gt 0) {
        $cleanupExceptions[0]
    }
    else {
        $Exception
    }
    while ($true) {
        $causalChildren = @(
            if ($firstCausalException -is [AggregateException]) {
                $firstCausalException.InnerExceptions
            }
            elseif ($null -ne $firstCausalException.InnerException) {
                $firstCausalException.InnerException
            }
        )
        if ($causalChildren.Count -eq 0) {
            break
        }

        $firstCausalException = $causalChildren[0]
    }

    return [pscustomobject]@{
        outer = [pscustomobject]@{
            type = $Exception.GetType().FullName
            message = $Exception.Message
            stack = $Exception.StackTrace
        }
        primaryFailure = if ($null -eq $primaryException) {
            $null
        }
        else {
            [pscustomobject]@{
                type = $primaryException.GetType().FullName
                message = $primaryException.Message
                stack = $primaryException.StackTrace
            }
        }
        firstCausal = [pscustomobject]@{
            type = $firstCausalException.GetType().FullName
            message = $firstCausalException.Message
            stack = $firstCausalException.StackTrace
        }
        innerFailures = @($innerFailures)
        cleanupFailures = @(
            $cleanupExceptions | ForEach-Object {
                [pscustomobject]@{
                    type = $_.GetType().FullName
                    message = $_.Message
                    stack = $_.StackTrace
                }
            }
        )
    }
}

function New-LifecycleFailureAggregate {
    param(
        [AllowNull()]
        [System.Exception]$PrimaryFailure,
        [Parameter(Mandatory)]
        [System.Exception[]]$CleanupFailures
    )

    $message = if ($null -eq $PrimaryFailure) {
        "Lifecycle owned child-process cleanup failed."
    }
    else {
        "Lifecycle phase and owned child-process cleanup both failed."
    }
    $failures = [Collections.Generic.List[Exception]]::new()
    if ($null -ne $PrimaryFailure) {
        $failures.Add($PrimaryFailure)
    }
    foreach ($failure in $CleanupFailures) {
        $failures.Add($failure)
    }

    $aggregate = [AggregateException]::new($message, $failures.ToArray())
    $aggregate.Data["DownKyi.Lifecycle.PrimaryFailure"] = $PrimaryFailure
    $aggregate.Data["DownKyi.Lifecycle.CleanupFailures"] = $CleanupFailures
    return $aggregate
}

function Set-LifecycleTransitionEvidence {
    param(
        [AllowNull()]
        [System.Collections.IDictionary]$Transitions,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$State,
        [AllowNull()]
        [Nullable[double]]$AfterMilliseconds,
        [AllowNull()]
        [Nullable[long]]$AtUnixMilliseconds,
        [Parameter(Mandatory)]
        [string]$Authority,
        [hashtable]$Diagnostic = @{}
    )

    if ($null -eq $Transitions) {
        return
    }

    $Transitions[$Name] = [pscustomobject]@{
        state = $State
        afterMilliseconds = $AfterMilliseconds
        atUnixMilliseconds = $AtUnixMilliseconds
        authority = $Authority
        diagnostic = [pscustomobject]$Diagnostic
    }
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

    $ownerJournal = $Exception.Failure.OwnerJournal
    return [pscustomobject]@{
        status = "capture-failed"
        evidencePath = $null
        errorType = $Exception.GetType().Name
        errorMessage = $Exception.Message
        exceptionEvidence = ConvertTo-ExceptionEvidence -Exception $Exception
        collectorFailureKind = $Exception.Failure.Kind.ToString()
        collectorEvidence = $Exception.Failure.Evidence
        collectorOwnerJournal = $ownerJournal
        collectorCleanupFailures = $cleanupFailures
        evidenceCaptured = $false
        evidencePersisted = $false
        diagnosticLocalization = Get-DiagnosticCollectorStructuralLocalization `
            -OwnerJournal $ownerJournal `
            -EvidenceCaptured $false `
            -EvidencePersisted $false
    }
}

function Get-DiagnosticCollectorStructuralLocalization {
    param(
        [AllowNull()]
        [object]$OwnerJournal,
        [Parameter(Mandatory)]
        [bool]$EvidenceCaptured,
        [Parameter(Mandatory)]
        [bool]$EvidencePersisted,
        [switch]$EvidencePersistenceFailed
    )

    $interval = ${OwnerJournal}?.FailureInterval
    $supervisorProcessId = ${OwnerJournal}?.SupervisorProcessId
    $targetProcessId = ${OwnerJournal}?.TargetProcessId
    if ($env:DOWNKYI_TEST_MUTATE_DIAGNOSTIC_OWNER_PROJECTION -eq "1") {
        # Reproduce the old `$OwnerJournal?.Member` parse, which reads a distinct
        # variable named `OwnerJournal?` instead of applying null-conditional access.
        $oldSyntaxOwnerJournal = Get-Variable `
            -Name "OwnerJournal?" `
            -ValueOnly `
            -ErrorAction SilentlyContinue
        $interval = ${oldSyntaxOwnerJournal}?.FailureInterval
        $supervisorProcessId = ${oldSyntaxOwnerJournal}?.SupervisorProcessId
        $targetProcessId = ${oldSyntaxOwnerJournal}?.TargetProcessId
    }
    $boundary = if ($null -eq $interval) {
        $null
    }
    else {
        $interval.Boundary.ToString()
    }
    $classification = if ($EvidencePersistenceFailed) {
        "EvidencePersistenceFailure"
    }
    elseif ($EvidencePersisted) {
        $null
    }
    elseif ($null -eq $interval) {
        if ($null -eq $OwnerJournal) { "EvidenceSystemFailure" } else { $null }
    }
    else {
        switch ($boundary) {
            "CollectorDispatch" { "CollectorDispatchFailure" }
            "ProcessStart" { "ProcessStartBoundaryFailure" }
            "ContainmentPreparation" { "ContainmentPreparationFailure" }
            "ContainmentEstablishment" { "ContainmentEstablishmentFailure" }
            "ControlChannelStartup" { "ControlChannelFailure" }
            "StatusChannelStartup" { "StatusChannelFailure" }
            "OwnershipHandshake" { "OwnershipAcknowledgementFailure" }
            "TargetLaunch" { "TargetLaunchFailure" }
            "EvidenceCapture" { "EvidenceCaptureFailure" }
            "TargetCompletion" { "TargetCompletionFailure" }
            "Cleanup" { "DiagnosticCleanupFailure" }
            default { "DiagnosticOutcomeFailure" }
        }
    }

    return [pscustomobject]@{
        classification = $classification
        lastKnownGood = if ($EvidencePersistenceFailed) {
            "EvidenceCaptured"
        }
        else {
            if ($null -eq $interval) {
                $null
            }
            else {
                $interval.LastKnownGood.ToString()
            }
        }
        firstMissingRequired = if ($EvidencePersistenceFailed) {
            "EvidencePersisted"
        }
        else {
            if ($null -eq $interval) {
                $null
            }
            else {
                $interval.FirstMissingRequired.ToString()
            }
        }
        boundary = if ($EvidencePersistenceFailed) {
            "EvidencePersistence"
        }
        else {
            $boundary
        }
        deadlineExhausted = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.DeadlineExhausted
        }
        targetStarted = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.TargetStarted
        }
        targetExited = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.TargetExited
        }
        terminationStarted = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.TerminationStarted
        }
        terminationCompleted = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.TerminationCompleted
        }
        reapCompleted = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.ReapCompleted
        }
        streamsDrained = if ($null -eq $OwnerJournal) {
            $false
        }
        else {
            $OwnerJournal.StreamsDrained
        }
        supervisorProcessId = $supervisorProcessId
        targetProcessId = $targetProcessId
        evidenceCaptured = $EvidenceCaptured
        evidencePersisted = $EvidencePersisted
        evidencePersistenceFailed = [bool]$EvidencePersistenceFailed
    }
}

function Invoke-DiagnosticEvidencePersistence {
    param(
        [AllowNull()]
        [object]$OwnerJournal,
        [Parameter(Mandatory)]
        [bool]$EvidenceCaptured,
        [Parameter(Mandatory)]
        [scriptblock]$WriteAction
    )

    try {
        return & $WriteAction
    }
    catch {
        throw (New-DiagnosticEvidencePersistenceFailure `
            -OwnerJournal $OwnerJournal `
            -EvidenceCaptured $EvidenceCaptured `
            -Cause $_.Exception)
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
        [Threading.CancellationToken]$CollectorStartupCancellationToken =
            [Threading.CancellationToken]::None,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    $CollectorStartupCancellationToken.ThrowIfCancellationRequested()
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

            $CollectorStartupCancellationToken.ThrowIfCancellationRequested()
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
        -StartupCancellationToken $CollectorStartupCancellationToken `
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
        [Threading.CancellationToken]$CollectorStartupCancellationToken =
            [Threading.CancellationToken]::None,
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
            collectorOwnerJournal = $null
            collectorCleanupFailures = @()
        }
    }

    $collectorFailure = $null
    $collectorOwnerJournal = $null
    try {
        $collectorOutcome = Invoke-OwnedDiagnosticCollector `
            -FileName $script:diagnosticsTool `
            -Arguments @(
                "report",
                "--process-id",
                $TargetProcessId.ToString(
                    [System.Globalization.CultureInfo]::InvariantCulture)) `
            -CaptureWindow $CaptureWindow `
            -StartupCancellationToken $CollectorStartupCancellationToken `
            -CancellationToken $CancellationToken
        $collector = $collectorOutcome.Evidence
        $collectorOwnerJournal = $collectorOutcome.OwnerJournal
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
        $collectorOwnerJournal = $collectorFailure.Failure.OwnerJournal
    }
    $captured = if ($null -ne $collectorFailure) {
        $true
    }
    else {
        -not $collector.TimedOut -and
            $collector.ExitCode -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($collector.StandardOutput)
    }
    $null = Invoke-DiagnosticEvidencePersistence `
        -OwnerJournal $collectorOwnerJournal `
        -EvidenceCaptured $captured `
        -WriteAction {
            [System.IO.File]::WriteAllText(
                $Destination,
                $collector.StandardOutput + $collector.StandardError,
                [System.Text.UTF8Encoding]::new($false))
        }
    return [pscustomobject]@{
        available = $true
        captured = $captured
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
        collectorOwnerJournal = $collectorOwnerJournal
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
        [Threading.CancellationToken]$CollectorStartupCancellationToken =
            [Threading.CancellationToken]::None,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None,
        [switch]$SkipManagedStack,
        [switch]$InjectEvidencePersistenceFailure
    )

    $safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'
    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-{2}" -f $Iteration, $Phase, $safeReason)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $stackResult = if ($Process.HasExited -or $SkipManagedStack) {
        [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorOwnerJournal = $null
            collectorCleanupFailures = @()
        }
    }
    else {
        Save-ManagedStack `
            -TargetProcessId $Process.Id `
            -Destination (Join-Path $directory "managed-stack.txt") `
            -CaptureWindow $CaptureWindow `
            -CollectorStartupCancellationToken $CollectorStartupCancellationToken `
            -CancellationToken $CancellationToken
    }

    $CollectorStartupCancellationToken.ThrowIfCancellationRequested()
    $threadSnapshot = @()
    $processTree = @()
    $captureSupplementalSnapshots =
        [string]::IsNullOrWhiteSpace($stackResult.collectorFailureKind) -and
        -not $Process.HasExited
    if ($captureSupplementalSnapshots) {
        if ($IsWindows) {
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
                -CollectorStartupCancellationToken `
                    $CollectorStartupCancellationToken `
                -CancellationToken $CancellationToken)
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
    $null = Invoke-DiagnosticEvidencePersistence `
        -OwnerJournal $stackResult.collectorOwnerJournal `
        -EvidenceCaptured $stackResult.captured `
        -WriteAction {
            if ($InjectEvidencePersistenceFailure) {
                throw [System.IO.IOException]::new(
                    "Injected diagnostic evidence persistence failure.")
            }
            $evidence |
                ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath (
                    Join-Path $directory "process-evidence.json") -Encoding utf8
        }
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
        [Threading.CancellationToken]$CollectorStartupCancellationToken =
            [Threading.CancellationToken]::None,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None,
        [ValidateRange(0, 5000)]
        [int]$CaptureDelayMilliseconds = 0,
        [ValidateRange(0, 5000)]
        [int]$InjectedPostCaptureDelayMilliseconds = 0,
        [switch]$InjectFailure,
        [switch]$SkipManagedStack,
        [switch]$InjectEvidencePersistenceFailure
    )

    if ($InjectFailure) {
        return [pscustomobject]@{
            status = "capture-failed"
            evidencePath = $null
            errorType = "InjectedForensicsObserverFailure"
            errorMessage = "Injected forensics observer failure."
            exceptionEvidence = $null
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorOwnerJournal = $null
            collectorCleanupFailures = @()
            evidenceCaptured = $false
            evidencePersisted = $false
            diagnosticLocalization = Get-DiagnosticCollectorStructuralLocalization `
                -OwnerJournal $null `
                -EvidenceCaptured $false `
                -EvidencePersisted $false
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
            errorMessage = $null
            exceptionEvidence = $null
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorOwnerJournal = $null
            collectorCleanupFailures = @()
            evidenceCaptured = $false
            evidencePersisted = $false
            diagnosticLocalization = $null
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
                -CollectorStartupCancellationToken `
                    $CollectorStartupCancellationToken `
                -CancellationToken $CancellationToken `
                -SkipManagedStack:$SkipManagedStack `
                -InjectEvidencePersistenceFailure:$InjectEvidencePersistenceFailure
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
            errorMessage = $null
            exceptionEvidence = $null
            collectorFailureKind =
                $evidenceCapture.managedStack.collectorFailureKind
            collectorEvidence = $evidenceCapture.managedStack.collectorEvidence
            collectorOwnerJournal =
                $evidenceCapture.managedStack.collectorOwnerJournal
            collectorCleanupFailures = @(
                $evidenceCapture.managedStack.collectorCleanupFailures)
            evidenceCaptured = $evidenceCapture.managedStack.captured
            evidencePersisted = $true
            diagnosticLocalization = Get-DiagnosticCollectorStructuralLocalization `
                -OwnerJournal $evidenceCapture.managedStack.collectorOwnerJournal `
                -EvidenceCaptured $evidenceCapture.managedStack.captured `
                -EvidencePersisted $true
        }
    }
    catch {
        $collectorFailure = Get-DiagnosticCollectorExecutionFailure `
            -Exception $_.Exception
        if ($null -ne $collectorFailure) {
            return ConvertTo-DiagnosticCollectorFailureReport `
                -Exception $collectorFailure
        }

        $ownerJournal = if (
            $_.Exception.Data.Contains("DownKyi.Diagnostic.OwnerJournal")) {
            $_.Exception.Data["DownKyi.Diagnostic.OwnerJournal"]
        }
        else {
            $null
        }
        $evidenceCaptured = if (
            $_.Exception.Data.Contains("DownKyi.Diagnostic.EvidenceCaptured")) {
            [bool]$_.Exception.Data["DownKyi.Diagnostic.EvidenceCaptured"]
        }
        else {
            $false
        }
        $persistenceFailed =
            $_.Exception.Data.Contains(
                "DownKyi.Diagnostic.EvidencePersistenceFailure") -and
            [bool]$_.Exception.Data[
                "DownKyi.Diagnostic.EvidencePersistenceFailure"]
        return [pscustomobject]@{
            status = "capture-failed"
            evidencePath = $null
            errorType = $_.Exception.GetType().Name
            errorMessage = $_.Exception.Message
            exceptionEvidence = ConvertTo-ExceptionEvidence `
                -Exception $_.Exception
            collectorFailureKind = $null
            collectorEvidence = $null
            collectorOwnerJournal = $ownerJournal
            collectorCleanupFailures = @()
            evidenceCaptured = $evidenceCaptured
            evidencePersisted = $false
            diagnosticLocalization = Get-DiagnosticCollectorStructuralLocalization `
                -OwnerJournal $ownerJournal `
                -EvidenceCaptured $evidenceCaptured `
                -EvidencePersisted $false `
                -EvidencePersistenceFailed:$persistenceFailed
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
        [switch]$InjectDiagnosticEvidencePersistenceFailure,
        [switch]$AuthorizeRepositoryTestAssembly,
        [ValidateRange(1, 3600)]
        [int]$OperationTimeoutSeconds = $PhaseTimeoutSeconds,
        [double]$EvidenceThresholdSeconds = $SlowPhaseThresholdSeconds,
        [ValidateRange(0, 60000)]
        [int]$EvidenceCaptureLeadMilliseconds =
            $slowEvidenceCaptureLeadMilliseconds,
        [System.Collections.IDictionary]$TransitionEvidence,
        [string]$ReadyEvidencePath,
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
        Import-DownKyiCentralTestRunner `
            -RepositoryRoot $repositoryRoot `
            -Configuration $Configuration
        [DownKyi.CentralTestRunner.CentralTestAuthorization]::Issue(
            [string[]]$Arguments,
            $repositoryRoot)
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
        $authorization.ApplyTo($authorizationStartInfo)
        $launchEnvironment["DOWNKYI_CENTRAL_TEST_ENDPOINT"] =
            $authorizationStartInfo.Environment["DOWNKYI_CENTRAL_TEST_ENDPOINT"]
        $launchEnvironment["DOWNKYI_CENTRAL_TEST_TOKEN"] =
            $authorizationStartInfo.Environment["DOWNKYI_CENTRAL_TEST_TOKEN"]
        $launchEnvironment["DOWNKYI_CENTRAL_TEST_PIPE"] = $null
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
    $processId = $null
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
    $slowEvidenceErrorMessage = $null
    $slowEvidenceExceptionEvidence = $null
    $slowEvidenceCollectorFailureKind = $null
    $slowEvidenceCollectorEvidence = $null
    $slowEvidenceCollectorOwnerJournal = $null
    $slowEvidenceCollectorCleanupFailures = @()
    $slowEvidenceDiagnosticLocalization = $null
    $slowDiagnosticEvidenceCaptured = $false
    $slowDiagnosticEvidencePersisted = $false
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
    $exitEvidenceCollectorOwnerJournal = $null
    $exitEvidenceCollectorCleanupFailures = @()
    $exitEvidenceDiagnosticLocalization = $null
    $exitDiagnosticEvidenceCaptured = $false
    $exitDiagnosticEvidencePersisted = $false
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
            $authorization.CompleteAsync(
                    $budget,
                    $lease.TargetExitedToken,
                    $CancellationToken).
                GetAwaiter().GetResult()
        }
        $processId = $lease.TargetProcessId
        Set-LifecycleTransitionEvidence `
            -Transitions $TransitionEvidence `
            -Name "targetStart" `
            -State "observed" `
            -AfterMilliseconds ([Math]::Round(
                ($OperationTimeoutSeconds * 1000.0) -
                    $budget.RemainingOperation.TotalMilliseconds,
                3)) `
            -AtUnixMilliseconds ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) `
            -Authority "OwnedProcessLease.TargetStarted" `
            -Diagnostic @{ processId = $processId }
        $observerCancellation = [Threading.CancellationTokenSource]::CreateLinkedTokenSource(
                $CancellationToken,
                $lease.TargetExitedToken)
        $waitTask = $lease.WaitAsync($CancellationToken)
        $evidenceObservationStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $waitTask.IsCompleted) {
            if ($null -ne $TransitionEvidence -and
                $TransitionEvidence["readyEstablishment"].state -eq
                    "not-observed" -and
                -not [string]::IsNullOrWhiteSpace($ReadyEvidencePath) -and
                (Test-Path -LiteralPath $ReadyEvidencePath -PathType Leaf)) {
                Set-LifecycleTransitionEvidence `
                    -Transitions $TransitionEvidence `
                    -Name "readyEstablishment" `
                    -State "observed" `
                    -AfterMilliseconds ([Math]::Round(
                        ($OperationTimeoutSeconds * 1000.0) -
                            $budget.RemainingOperation.TotalMilliseconds,
                        3)) `
                    -AtUnixMilliseconds (
                        [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) `
                    -Authority "atomic-ready-file-observation"
            }
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
                Set-LifecycleTransitionEvidence `
                    -Transitions $TransitionEvidence `
                    -Name "collectorArm" `
                    -State "observed" `
                    -AfterMilliseconds ([Math]::Round(
                        ($OperationTimeoutSeconds * 1000.0) -
                            $budget.RemainingOperation.TotalMilliseconds,
                        3)) `
                    -AtUnixMilliseconds $slowEvidenceCaptureArmedAtUnixMilliseconds `
                    -Authority "TransitionBudget" `
                    -Diagnostic @{
                        policyStopwatchAfterLeaseMilliseconds =
                            $slowEvidenceCaptureArmedAfterMilliseconds
                    }
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
                        -CollectorStartupCancellationToken $CancellationToken `
                        -CancellationToken $observerCancellation.Token `
                        -CaptureDelayMilliseconds $EvidenceCaptureDelayMilliseconds `
                        -InjectedPostCaptureDelayMilliseconds `
                            $InjectedPostCaptureDelayMilliseconds `
                        -SkipManagedStack:$SkipSlowEvidenceManagedStack `
                        -InjectFailure:$InjectForensicsObserverFailure `
                        -InjectEvidencePersistenceFailure:$InjectDiagnosticEvidencePersistenceFailure
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
                    $slowEvidenceErrorMessage = $capture.errorMessage
                    $slowEvidenceExceptionEvidence = $capture.exceptionEvidence
                    $slowEvidenceCollectorFailureKind = $capture.collectorFailureKind
                    $slowEvidenceCollectorEvidence = $capture.collectorEvidence
                    $slowEvidenceCollectorOwnerJournal =
                        $capture.collectorOwnerJournal
                    $slowEvidenceCollectorCleanupFailures = @(
                        $capture.collectorCleanupFailures)
                    $slowEvidenceDiagnosticLocalization =
                        $capture.diagnosticLocalization
                    $slowDiagnosticEvidenceCaptured = $capture.evidenceCaptured
                    $slowDiagnosticEvidencePersisted = $capture.evidencePersisted
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
                    $slowEvidenceErrorMessage = $_.Exception.Message
                    $slowEvidenceExceptionEvidence = ConvertTo-ExceptionEvidence `
                        -Exception $_.Exception
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
                    Set-LifecycleTransitionEvidence `
                        -Transitions $TransitionEvidence `
                        -Name "collectorCompletion" `
                        -State $slowEvidenceStatus `
                        -AfterMilliseconds $slowEvidenceCaptureCompletedAfterMilliseconds `
                        -AtUnixMilliseconds `
                            $slowEvidenceCaptureCompletedAtUnixMilliseconds `
                        -Authority "TransitionBudget"
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
                            -CollectorStartupCancellationToken $CancellationToken `
                            -CancellationToken $observerCancellation.Token `
                            -InjectFailure:$InjectForensicsObserverFailure
                        $exitEvidenceStatus = $capture.status
                        $exitEvidenceErrorType = $capture.errorType
                        $exitEvidenceCollectorFailureKind = $capture.collectorFailureKind
                        $exitEvidenceCollectorEvidence = $capture.collectorEvidence
                        $exitEvidenceCollectorOwnerJournal =
                            $capture.collectorOwnerJournal
                        $exitEvidenceCollectorCleanupFailures = @(
                            $capture.collectorCleanupFailures)
                        $exitEvidenceDiagnosticLocalization =
                            $capture.diagnosticLocalization
                        $exitDiagnosticEvidenceCaptured = $capture.evidenceCaptured
                        $exitDiagnosticEvidencePersisted = $capture.evidencePersisted
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
        Set-LifecycleTransitionEvidence `
            -Transitions $TransitionEvidence `
            -Name "targetExit" `
            -State "observed" `
            -AfterMilliseconds $(
                if ($null -eq $targetExitedAfter) {
                    $null
                }
                else {
                    [Math]::Round($targetExitedAfter.TotalMilliseconds, 3)
                }) `
            -AtUnixMilliseconds $processExitedAtUnixMs `
            -Authority "OwnedProcessLease.TargetExitedAfter"
        $slowEvidenceCaptureCompletedBeforeTargetExit =
            $null -ne $slowEvidenceCaptureCompletedAfterMilliseconds -and
            $null -ne $targetExitedAfter -and
            $slowEvidenceCaptureCompletedAfterMilliseconds -lt
                $targetExitedAfter.TotalMilliseconds
        $targetExitedDuringSlowEvidenceCapture =
            $slowEvidenceAttempted -and
            -not $slowEvidenceCaptured -and
            $slowEvidenceStatus -eq "capture-failed" -and
            $slowEvidenceCollectorFailureKind -eq "CallerCancelled" -and
            $null -ne $slowEvidenceCaptureCompletedAfterMilliseconds -and
            $null -ne $targetExitedAfter -and
            $slowEvidenceCaptureCompletedAfterMilliseconds -ge
                $targetExitedAfter.TotalMilliseconds
        if ($targetExitedDuringSlowEvidenceCapture) {
            $slowEvidenceStatus = "target-exited-during-capture"
            $slowEvidenceErrorType = "TargetExitedDuringCapture"
            $slowEvidenceErrorMessage = (
                "TargetExitedDuringCapture: slow-evidence capture armed at " +
                "$slowEvidenceCaptureArmedAfterMilliseconds ms; target exited at " +
                "$([Math]::Round($targetExitedAfter.TotalMilliseconds, 3)) ms before " +
                "capture completed at $slowEvidenceCaptureCompletedAfterMilliseconds ms; " +
                "collector outcome was CallerCancelled.")
        }
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
        $primaryFailure = if ($null -ne $ownedFailure) {
            "{0}: {1}" -f
                $ownedFailure.InnerException.GetType().Name,
                $ownedFailure.InnerException.Message
        }
        elseif ($outcome.ExitCode -ne 0) {
            "Target exited with code $($outcome.ExitCode)."
        }
        else {
            $null
        }
        $ownedCleanupFailures = @(
            if ($null -ne $ownedFailure) {
                $ownedFailure.CleanupStageFailures | ForEach-Object {
                    "{0}: {1}: {2}" -f
                        $_.Stage,
                        $_.Cause.GetType().Name,
                        $_.Cause.Message
                }
            }
        )
        $timedOut = $ownedFailureKind -in @(
            "OperationDeadlineExceeded",
            "StreamDrainDeadlineExceeded")
        $residualChildren = @(
            if (-not $ownedTreeQuiescent -and
                $ownedFailureKind -eq "OwnedTreeNotQuiescent") {
                [pscustomobject]@{
                    processId = $null
                    parentProcessId = $null
                    name = "owned-process-tree"
                    createdAtUtc = $null
                    containmentId = $ownedFailure.Failure.Ownership.ContainmentId
                    containmentKind = $ownedFailure.Failure.Ownership.ContainmentKind.ToString()
                }
            }
        )
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
            exitCode = if ($null -ne $outcome) {
                $outcome.ExitCode
            }
            else {
                $ownedFailure.Failure.TargetExitCode
            }
            primaryFailure = $primaryFailure
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
            exitEvidenceCollectorOwnerJournal =
                $exitEvidenceCollectorOwnerJournal
            exitEvidenceCollectorCleanupFailures = @(
                $exitEvidenceCollectorCleanupFailures)
            exitEvidenceDiagnosticLocalization =
                $exitEvidenceDiagnosticLocalization
            exitDiagnosticEvidenceCaptured = $exitDiagnosticEvidenceCaptured
            exitDiagnosticEvidencePersisted = $exitDiagnosticEvidencePersisted
            timeoutEvidence = $timeoutEvidence
            diagnosticCaptureDurationMs = [Math]::Round($diagnosticCaptureDurationMs, 3)
            slowThresholdExceeded = $slowThresholdExceeded
            slowEvidenceStatus = $slowEvidenceStatus
            slowEvidenceErrorType = $slowEvidenceErrorType
            slowEvidenceErrorMessage = $slowEvidenceErrorMessage
            slowEvidenceExceptionEvidence = $slowEvidenceExceptionEvidence
            slowEvidenceCollectorFailureKind = $slowEvidenceCollectorFailureKind
            slowEvidenceCollectorEvidence = $slowEvidenceCollectorEvidence
            slowEvidenceCollectorOwnerJournal =
                $slowEvidenceCollectorOwnerJournal
            slowEvidenceCollectorCleanupFailures = @(
                $slowEvidenceCollectorCleanupFailures)
            slowEvidenceDiagnosticLocalization =
                $slowEvidenceDiagnosticLocalization
            slowDiagnosticEvidenceCaptured = $slowDiagnosticEvidenceCaptured
            slowDiagnosticEvidencePersisted = $slowDiagnosticEvidencePersisted
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
            ownedProcessCleanupFailures = $ownedCleanupFailures
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
        Set-LifecycleTransitionEvidence `
            -Transitions $TransitionEvidence `
            -Name "faultBoundary" `
            -State "failed" `
            -AfterMilliseconds ([Math]::Round(
                ($OperationTimeoutSeconds * 1000.0) -
                    $budget.RemainingOperation.TotalMilliseconds,
                3)) `
            -AtUnixMilliseconds ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) `
            -Authority "Invoke-IsolatedProcess.operation" `
            -Diagnostic @{
                exceptionType = $operationFailure.GetType().FullName
                exceptionMessage = $operationFailure.Message
            }
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
        if ($null -ne $authorization) {
            $authorization.Dispose()
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    Set-LifecycleTransitionEvidence `
        -Transitions $TransitionEvidence `
        -Name "cleanupCompletion" `
        -State $(if ($cleanupFailures.Count -eq 0) { "completed" } else { "failed" }) `
        -AfterMilliseconds ([Math]::Round(
            ($OperationTimeoutSeconds * 1000.0) -
                $budget.RemainingOperation.TotalMilliseconds,
            3)) `
        -AtUnixMilliseconds ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) `
        -Authority "Invoke-IsolatedProcess.cleanup" `
        -Diagnostic @{ failureCount = $cleanupFailures.Count }
    if ($null -ne $operationFailure -or $cleanupFailures.Count -gt 0) {
        Set-LifecycleTransitionEvidence `
            -Transitions $TransitionEvidence `
            -Name "faultBoundary" `
            -State "failed" `
            -AfterMilliseconds ([Math]::Round(
                ($OperationTimeoutSeconds * 1000.0) -
                    $budget.RemainingOperation.TotalMilliseconds,
                3)) `
            -AtUnixMilliseconds ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) `
            -Authority "Invoke-IsolatedProcess.cleanup" `
            -Diagnostic @{
                exceptionType = if ($null -ne $operationFailure) {
                    $operationFailure.GetType().FullName
                }
                else {
                    $cleanupFailures[0].GetType().FullName
                }
                exceptionMessage = if ($null -ne $operationFailure) {
                    $operationFailure.Message
                }
                else {
                    $cleanupFailures[0].Message
                }
            }
        $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
            Get-Content -LiteralPath $stdoutPath -Raw
        }
        else {
            ""
        }
        $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            Get-Content -LiteralPath $stderrPath -Raw
        }
        else {
            ""
        }
        $operationResult = [pscustomobject]@{
            assembly = $AssemblyName
            iteration = $Iteration
            phase = $Phase
            processId = $processId
            exitCode = $null
            primaryFailure = if ($null -ne $operationFailure) {
                "{0}: {1}" -f $operationFailure.GetType().Name, $operationFailure.Message
            }
            else {
                "Lifecycle cleanup did not complete."
            }
            durationMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            timedOut = $operationFailure -is [TimeoutException] -or
                $operationFailure -is [OperationCanceledException]
            stdout = $stdout
            stderr = $stderr
            stdoutPath = [System.IO.Path]::GetRelativePath($runRoot, $stdoutPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            stderrPath = [System.IO.Path]::GetRelativePath($runRoot, $stderrPath).
                Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            observedChildren = @()
            transientChildren = @()
            residualChildren = @()
            childProcessObservationSampleCount = 0
            childProcessObservationDurationMs = 0.0
            residualChildEvidence = @()
            residualChildEvidenceStatus = "not-triggered"
            residualChildEvidenceErrorType = $null
            evidence = @()
            slowEvidence = @()
            exitEvidence = @()
            exitEvidenceStatus = "not-triggered"
            exitEvidenceErrorType = $null
            exitEvidenceCollectorFailureKind = $null
            exitEvidenceCollectorEvidence = $null
            exitEvidenceCollectorOwnerJournal = $null
            exitEvidenceCollectorCleanupFailures = @()
            exitEvidenceDiagnosticLocalization = $null
            exitDiagnosticEvidenceCaptured = $false
            exitDiagnosticEvidencePersisted = $false
            timeoutEvidence = @()
            diagnosticCaptureDurationMs = 0.0
            slowThresholdExceeded = $false
            slowEvidenceStatus = "not-triggered"
            slowEvidenceErrorType = $null
            slowEvidenceErrorMessage = $null
            slowEvidenceExceptionEvidence = $null
            slowEvidenceCollectorFailureKind = $null
            slowEvidenceCollectorEvidence = $null
            slowEvidenceCollectorOwnerJournal = $null
            slowEvidenceCollectorCleanupFailures = @()
            slowEvidenceDiagnosticLocalization = $null
            slowDiagnosticEvidenceCaptured = $false
            slowDiagnosticEvidencePersisted = $false
            slowEvidenceTriggeredBeforeThreshold = $false
            slowEvidenceCaptureArmedAfterMilliseconds = $null
            slowEvidenceCaptureArmedAtUnixMilliseconds = $null
            slowEvidenceCaptureCompletedAfterMilliseconds = $null
            slowEvidenceCaptureCompletedAtUnixMilliseconds = $null
            slowEvidenceCaptureCompletedBeforeTargetExit = $false
            evidenceCaptureThresholdSeconds = $evidenceCaptureThresholdSeconds
            evidenceCaptureLeadMilliseconds = $EvidenceCaptureLeadMilliseconds
            ownedTreeQuiescent = $false
            ownedProcessFailureKind = if ($null -ne $operationFailure) {
                $operationFailure.GetType().Name
            }
            else {
                "CleanupFailed"
            }
            ownedProcessCleanupFailures = @(
                $cleanupFailures | ForEach-Object {
                    "Dispose: {0}: {1}" -f $_.GetType().Name, $_.Message
                })
            processOwnership = $null
            evidenceHold = $null
            processExitedAtUnixMs = $null
            targetExitedAfterMilliseconds = $null
            observedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
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

function Get-LifecycleLikelyOwner {
    param(
        [string]$Phase,
        [string]$ProcessFailureType,
        [string]$ForensicsFailureType
    )

    if ($Phase -eq "assembly-teardown") {
        return "FixtureTeardown"
    }
    if (-not [string]::IsNullOrWhiteSpace($ProcessFailureType)) {
        return "OwnedProcessLease"
    }
    if (-not [string]::IsNullOrWhiteSpace($ForensicsFailureType)) {
        return "ForensicsCollector"
    }

    return "LifecycleProbe"
}

function Get-XunitPrimaryFailure {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Content
    )

    foreach ($line in @($Content -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        try {
            $payload = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }
        if ($payload.'$type' -ne "test-failed") {
            continue
        }

        $message = @($payload.Messages | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            }) | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($message)) {
            $message = "xUnit test failed."
        }
        if (-not [string]::IsNullOrWhiteSpace($payload.TestDisplayName)) {
            return "{0}: {1}" -f $payload.TestDisplayName, $message
        }

        return $message
    }

    return $null
}

function Get-LifecycleOutputSummary {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Content,
        [string]$Phase,
        [string]$FullOutputPath,
        [ValidateRange(256, 16384)]
        [int]$MaximumCharacters = 1024
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return "(empty)"
    }
    if ($Phase -eq "execution") {
        $failures = [Collections.Generic.List[string]]::new()
        foreach ($line in @($Content -split '\r?\n')) {
            try {
                $payload = $line | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                continue
            }
            if ($payload.'$type' -ne "test-failed") {
                continue
            }
            $messages = @($payload.Messages | Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_)
                }) -join " | "
            $failures.Add(("{0}: {1}" -f $payload.TestDisplayName, $messages))
        }
        if ($failures.Count -gt 0) {
            return ($failures -join [Environment]::NewLine)
        }
    }

    $trimmed = $Content.TrimEnd()
    if ($trimmed.Length -le $MaximumCharacters) {
        return $trimmed
    }

    $location = if ([string]::IsNullOrWhiteSpace($FullOutputPath)) {
        "the raw lifecycle artifact"
    }
    else {
        $FullOutputPath
    }
    return $trimmed.Substring(0, $MaximumCharacters) +
        "`n[output truncated; full content: $location]"
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
    $xunitPrimaryFailure = if ($ProcessResult.phase -eq "execution" -and
        $ProcessResult.exitCode -ne 0) {
        Get-XunitPrimaryFailure -Content $ProcessResult.stdout
    }
    else {
        $null
    }
    $primaryFailure = if (-not [string]::IsNullOrWhiteSpace(
            $xunitPrimaryFailure)) {
        $xunitPrimaryFailure
    }
    elseif (-not [string]::IsNullOrWhiteSpace(
            $ProcessResult.primaryFailure)) {
        $ProcessResult.primaryFailure
    }
    elseif ($failureType -eq "SlowEvidenceMissing" -and
        -not [string]::IsNullOrWhiteSpace(
            $ProcessResult.slowEvidenceErrorMessage)) {
        $ProcessResult.slowEvidenceErrorMessage
    }
    elseif ($null -ne $errorType) {
        $errorType
    }
    elseif ($ProcessResult.exitCode -ne 0) {
        "Target exited with code $($ProcessResult.exitCode)."
    }
    else {
        $failureType
    }
    $likelyOwner = Get-LifecycleLikelyOwner `
        -Phase $ProcessResult.phase `
        -ProcessFailureType $processFailureType `
        -ForensicsFailureType $forensicsFailureType
    return [pscustomobject]@{
        assembly = $ProcessResult.assembly
        iteration = $ProcessResult.iteration
        phase = $ProcessResult.phase
        processId = $ProcessResult.processId
        success = $success
        failureType = $failureType
        primaryFailure = $primaryFailure
        likelyOwner = $likelyOwner
        errorType = $errorType
        processFailureType = $processFailureType
        forensicsFailureType = $forensicsFailureType
        exitCode = $ProcessResult.exitCode
        durationMs = $ProcessResult.durationMs
        timedOut = $ProcessResult.timedOut
        stdoutPolluted = -not $protocolValid -or $unexpectedText.Count -gt 0
        stderrPolluted = -not $stderrClean
        stdout = $ProcessResult.stdout
        stderr = $ProcessResult.stderr
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
        exitEvidenceCollectorOwnerJournal =
            $ProcessResult.exitEvidenceCollectorOwnerJournal
        exitEvidenceCollectorCleanupFailures = @(
            $ProcessResult.exitEvidenceCollectorCleanupFailures)
        exitEvidenceDiagnosticLocalization =
            $ProcessResult.exitEvidenceDiagnosticLocalization
        exitDiagnosticEvidenceCaptured =
            $ProcessResult.exitDiagnosticEvidenceCaptured
        exitDiagnosticEvidencePersisted =
            $ProcessResult.exitDiagnosticEvidencePersisted
        timeoutEvidence = $ProcessResult.timeoutEvidence
        diagnosticCaptureDurationMs = $ProcessResult.diagnosticCaptureDurationMs
        slowThresholdExceeded = $ProcessResult.slowThresholdExceeded
        slowEvidenceStatus = $ProcessResult.slowEvidenceStatus
        slowEvidenceErrorType = $ProcessResult.slowEvidenceErrorType
        slowEvidenceErrorMessage = $ProcessResult.slowEvidenceErrorMessage
        slowEvidenceExceptionEvidence =
            $ProcessResult.slowEvidenceExceptionEvidence
        slowEvidenceCollectorFailureKind =
            $ProcessResult.slowEvidenceCollectorFailureKind
        slowEvidenceCollectorEvidence = $ProcessResult.slowEvidenceCollectorEvidence
        slowEvidenceCollectorOwnerJournal =
            $ProcessResult.slowEvidenceCollectorOwnerJournal
        slowEvidenceCollectorCleanupFailures = @(
            $ProcessResult.slowEvidenceCollectorCleanupFailures)
        slowEvidenceDiagnosticLocalization =
            $ProcessResult.slowEvidenceDiagnosticLocalization
        slowDiagnosticEvidenceCaptured =
            $ProcessResult.slowDiagnosticEvidenceCaptured
        slowDiagnosticEvidencePersisted =
            $ProcessResult.slowDiagnosticEvidencePersisted
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

function Invoke-TargetExitDuringCaptureValidation {
    $processSupervisionHost = [System.IO.Path]::ChangeExtension(
        $processSupervisionAssembly,
        $(if ($IsWindows) { ".exe" } else { $null }))
    if (-not (Test-Path -LiteralPath $processSupervisionHost -PathType Leaf)) {
        throw "Process supervision host was not built: $processSupervisionHost"
    }

    $fixtureRoot = Join-Path $runRoot "target-exit-during-capture"
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $readyPath = Join-Path $fixtureRoot "target.ready.json"
    $signalPath = Join-Path $fixtureRoot "target.exit"
    $originalDiagnosticsTool = $script:diagnosticsTool
    $originalSignal = $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_SIGNAL
    $originalDelay = $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_DELAY_MS
    try {
        $script:diagnosticsTool = $processSupervisionHost
        $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_SIGNAL = $signalPath
        $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_DELAY_MS = "3200"
        $processResult = Invoke-IsolatedProcess `
            -AssemblyName "TargetExitDuringCaptureFixture" `
            -Iteration 1 `
            -Phase "execution" `
            -FileName $processSupervisionHost `
            -Arguments @(
                "--exit-on-file-signal-with-ready",
                $readyPath,
                $signalPath) `
            -OperationTimeoutSeconds 30 `
            -EvidenceThresholdSeconds 5 `
            -EvidenceCaptureLeadMilliseconds 3000 `
            -ReadyEvidencePath $readyPath
    }
    finally {
        $script:diagnosticsTool = $originalDiagnosticsTool
        $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_SIGNAL = $originalSignal
        $env:DOWNKYI_TEST_DIAGNOSTIC_TARGET_EXIT_DELAY_MS = $originalDelay
    }

    $phaseResult = New-ProcessPhaseResult -ProcessResult $processResult
    $captureCompletedAfterTargetExit =
        $null -ne $processResult.slowEvidenceCaptureCompletedAfterMilliseconds -and
        $null -ne $processResult.targetExitedAfterMilliseconds -and
        $processResult.slowEvidenceCaptureCompletedAfterMilliseconds -ge
            $processResult.targetExitedAfterMilliseconds
    $validation = [ordered]@{
        assembly = $phaseResult.assembly
        phase = $phaseResult.phase
        failureType = $phaseResult.failureType
        primaryFailure = $phaseResult.primaryFailure
        slowThresholdExceeded = $phaseResult.slowThresholdExceeded
        slowEvidenceStatus = $phaseResult.slowEvidenceStatus
        slowEvidenceErrorType = $phaseResult.slowEvidenceErrorType
        slowEvidenceErrorMessage = $phaseResult.slowEvidenceErrorMessage
        slowEvidenceExceptionEvidence =
            $phaseResult.slowEvidenceExceptionEvidence
        slowEvidenceCollectorFailureKind =
            $phaseResult.slowEvidenceCollectorFailureKind
        captureArmedAfterMilliseconds =
            $phaseResult.slowEvidenceCaptureArmedAfterMilliseconds
        captureCompletedAfterMilliseconds =
            $phaseResult.slowEvidenceCaptureCompletedAfterMilliseconds
        targetExitedAfterMilliseconds =
            $phaseResult.targetExitedAfterMilliseconds
        captureCompletedAfterTargetExit = $captureCompletedAfterTargetExit
        exitCode = $phaseResult.exitCode
        ownedTreeQuiescent = $phaseResult.ownedTreeQuiescent
        cleanupFailureCount =
            @($phaseResult.ownedProcessCleanupFailures).Count
        collectorStarted =
            $null -ne $processResult.slowEvidenceCollectorEvidence -and
            $processResult.slowEvidenceCollectorEvidence.Started
        targetExitSignalObserved =
            Test-Path -LiteralPath $signalPath -PathType Leaf
    }
    Write-Output (
        "DOWNKYI_TARGET_EXIT_CAPTURE_RESULT=" +
            ($validation | ConvertTo-Json -Depth 14 -Compress))

    $valid =
        $validation.failureType -eq "SlowEvidenceMissing" -and
        $validation.slowThresholdExceeded -and
        $validation.slowEvidenceStatus -eq "target-exited-during-capture" -and
        $validation.slowEvidenceErrorType -eq "TargetExitedDuringCapture" -and
        $validation.slowEvidenceCollectorFailureKind -eq "CallerCancelled" -and
        $validation.captureCompletedAfterTargetExit -and
        $null -ne $validation.captureArmedAfterMilliseconds -and
        $validation.captureArmedAfterMilliseconds -lt
            $validation.targetExitedAfterMilliseconds -and
        $validation.exitCode -eq 0 -and
        $validation.ownedTreeQuiescent -and
        $validation.cleanupFailureCount -eq 0 -and
        $validation.collectorStarted -and
        $validation.targetExitSignalObserved -and
        $validation.primaryFailure -like "TargetExitedDuringCapture:*" -and
        $validation.slowEvidenceErrorMessage -notlike
            "*CommandNotFoundException*" -and
        $validation.slowEvidenceErrorMessage -notlike
            "*not recognized as a name of a cmdlet*"
    if (-not $valid) {
        throw "Target-exit-during-capture validation did not produce the typed lifecycle outcome."
    }
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
if ($ValidateTargetExitDuringCapture) {
    Invoke-TargetExitDuringCaptureValidation
    return
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
                DOWNKYI_LIFECYCLE_MARKER_OWNER = "1"
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
            primaryFailure = if ($markerValid -and $testRootRemoved) {
                $null
            }
            elseif (-not $markerValid) {
                "The lifecycle teardown marker sequence is incomplete or invalid."
            }
            else {
                "The fixture data root remained after assembly teardown."
            }
            likelyOwner = "FixtureTeardown"
            errorType = $null
            exitCode = if ($markerValid -and $testRootRemoved) { 0 } else { 1 }
            durationMs = $teardownDuration
            timedOut = $false
            stdoutPolluted = $false
            stderrPolluted = $false
            stdout = ""
            stderr = ""
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
            ownedTreeQuiescent = $execution.ownedTreeQuiescent
            ownedProcessCleanupFailures = @()
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
            primaryFailure = if ($exitSucceeded) {
                $null
            }
            elseif (-not [string]::IsNullOrWhiteSpace($execution.primaryFailure)) {
                $execution.primaryFailure
            }
            else {
                "The target did not exit and quiesce within the lifecycle contract."
            }
            likelyOwner = if (-not $execution.timedOut -and
                $execution.ownedTreeQuiescent -and
                $execution.ownedProcessCleanupFailures.Count -eq 0 -and
                $execution.residualChildren.Count -eq 0) {
                "LifecycleProbe"
            }
            else {
                "OwnedProcessLease"
            }
            errorType = $null
            exitCode = $execution.exitCode
            durationMs = [Math]::Round($exitDuration, 3)
            timedOut = $execution.timedOut
            stdoutPolluted = $false
            stderrPolluted = $false
            stdout = $execution.stdout
            stderr = $execution.stderr
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
            ownedTreeQuiescent = $execution.ownedTreeQuiescent
            ownedProcessCleanupFailures = @(
                $execution.ownedProcessCleanupFailures)
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
    schemaVersion = 5
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
    forensicsCaptureWindowMilliseconds = $forensicsCaptureWindowMilliseconds
    forensicsCaptureCleanupWindowMilliseconds =
        $forensicsCaptureCleanupWindowMilliseconds
    processCleanupGraceSeconds = $processCleanupGraceSeconds
    exitThresholdSeconds = $ExitThresholdSeconds
    diagnosticsTool = if ($null -eq $script:diagnosticsTool) {
        "unavailable"
    }
    else {
        [System.IO.Path]::GetFileName($script:diagnosticsTool)
    }
    ownershipAuditPassed = $ownershipPassed
    ownershipAuditErrorType = $ownershipError
    successful = $ownershipPassed -and $failedResults.Count -eq 0
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
$markdown.Add("- Marker read contentions: $script:markerReadContentionCount")
$markdown.Add("- Marker read retry exhaustion: $script:markerReadRetriesExhaustedCount")
$markdown.Add(
    "- Marker read errors: $script:markerReadErrorCount; " +
    "last type=$script:markerReadErrorType")
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
        $cleanupFailures = @(
            $failure.ownedProcessCleanupFailures |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $cleanupFailureText = if ($cleanupFailures.Count -eq 0) {
            "None"
        }
        else {
            $cleanupFailures -join "; "
        }
        $stdoutText = Get-LifecycleOutputSummary `
            -Content $failure.stdout `
            -Phase $failure.phase `
            -FullOutputPath $failure.stdoutPath
        $stderrText = Get-LifecycleOutputSummary `
            -Content $failure.stderr `
            -Phase $failure.phase `
            -FullOutputPath $failure.stderrPath
        $evidencePaths = @(
            @($failure.evidence) +
                @($failure.slowEvidence) +
                @($failure.residualChildEvidence) +
                @($failure.stdoutPath) +
                @($failure.stderrPath) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )
        $evidencePathText = if ($evidencePaths.Count -eq 0) {
            "None"
        }
        else {
            $evidencePaths -join "; "
        }

        $markdown.Add("### $($failure.assembly) / $($failure.phase)")
        $markdown.Add("")
        $markdown.Add("Assembly: $($failure.assembly)")
        $markdown.Add("Iteration: $($failure.iteration)")
        $markdown.Add("Phase: $($failure.phase)")
        $markdown.Add("FailureKind: $($failure.failureType)")
        $markdown.Add("PrimaryFailure: $($failure.primaryFailure)")
        $markdown.Add("TargetExitCode: $($failure.exitCode)")
        $markdown.Add("OwnedTreeQuiescent: $($failure.ownedTreeQuiescent)")
        $markdown.Add("CleanupFailures: $cleanupFailureText")
        $markdown.Add("Stdout:")
        $markdown.Add('```text')
        $markdown.Add($stdoutText)
        $markdown.Add('```')
        $markdown.Add("Stderr:")
        $markdown.Add('```text')
        $markdown.Add($stderrText)
        $markdown.Add('```')
        $markdown.Add("EvidencePath: $evidencePathText")
        $markdown.Add("LikelyOwner: $($failure.likelyOwner)")
        $markdown.Add("")

        Write-Host "Assembly: $($failure.assembly)"
        Write-Host "Iteration: $($failure.iteration)"
        Write-Host "Phase: $($failure.phase)"
        Write-Host "FailureKind: $($failure.failureType)"
        Write-Host "PrimaryFailure: $($failure.primaryFailure)"
        Write-Host "TargetExitCode: $($failure.exitCode)"
        Write-Host "OwnedTreeQuiescent: $($failure.ownedTreeQuiescent)"
        Write-Host "CleanupFailures: $cleanupFailureText"
        Write-Host "Stdout:`n$stdoutText"
        Write-Host "Stderr:`n$stderrText"
        Write-Host "EvidencePath: $evidencePathText"
        Write-Host "LikelyOwner: $($failure.likelyOwner)"
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
