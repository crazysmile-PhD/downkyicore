using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleProbeBehaviorTests
{
    private const string MutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_LEASE";
    private const string HelperAuthorityMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_HELPER_AUTHORITY";
    private const string CaptureBudgetMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_CAPTURE_BUDGET";
    private const string StartupWindowMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_STARTUP_WINDOW";
    private const string EarlyReadyMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_EARLY_READY";
    private const string CleanupReportMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_CLEANUP_REPORT";
    private const string SupervisorStartupMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_SUPERVISOR_STARTUP";
    private const string DiagnosticJournalMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_DIAGNOSTIC_OWNER_JOURNAL";
    private const string DiagnosticProjectionMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_DIAGNOSTIC_OWNER_PROJECTION";
    private const string CaptureBudgetSelfTestRejection =
        "Forensics collector capture-window self-test did not fail closed.";
    private const string CleanupReportSelfTestRejection =
        "Forensics collector cleanup-report self-test did not preserve evidence.";
    private const string DiagnosticProjectionSelfTestRejection =
        "Forensics evidence-persistence self-test lost its owner journal.";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void LifecycleForensicsIsAnObserverRatherThanASecondProcessOwner()
    {
        var source = ReadLifecycleGate();
        if (string.Equals(
                Environment.GetEnvironmentVariable(HelperAuthorityMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            source = source.Replace(
                "    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(",
                "    $startInfo = Microsoft.PowerShell.Management\\New-Object " +
                "-TypeName System.Diagnostics.ProcessStartInfo\n" +
                "    $collector = Microsoft.PowerShell.Management\\New-Object " +
                "-TypeName System.Diagnostics.Process\n" +
                "    $null = Microsoft.PowerShell.Management\\Start-Process " +
                "-FilePath 'pwsh' -PassThru\n" +
                "    Microsoft.PowerShell.Management\\Stop-Process -Id 1\n" +
                "    Microsoft.PowerShell.Management\\Wait-Process -Id 1\n" +
                "    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(",
                StringComparison.Ordinal);
        }

        var observerClosure = ReadFunctionClosure(source, "Invoke-ForensicsObserverCapture");
        string[] forbiddenObserverAuthorities =
        [
            "ownedTreeQuiescent",
            "residualChildren",
            "OwnedProcessLease",
            "CompleteEvidenceHoldAsync",
            "TransitionBudget]::Start",
            "[AggregateException]::new",
            ".Kill(",
            ".WaitForExit(",
            ".WaitForExitAsync("
        ];

        foreach (var function in observerClosure)
        {
            foreach (var authority in forbiddenObserverAuthorities)
            {
                Assert.DoesNotContain(authority, function.Value, StringComparison.Ordinal);
            }
        }

        Assert.Contains("Save-ProcessEvidence", observerClosure.Keys);
        Assert.Contains("Get-DiagnosticProcessTreeSnapshot", observerClosure.Keys);
        Assert.Contains("Invoke-OwnedDiagnosticCollector", observerClosure.Keys);
        Assert.DoesNotContain("Invoke-BoundedForensicsCollector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-BoundedForensicsCollector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsExceptionType", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsTimeoutException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsCollectorCaptureWindow", source, StringComparison.Ordinal);

        Assert.Contains("EvidenceHoldRequest", source, StringComparison.Ordinal);
        Assert.Contains("CompleteEvidenceHoldAsync", source, StringComparison.Ordinal);
        Assert.Contains("EvidenceCaptureCompletion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function New-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedChildReleaseLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-ResidualProcessTree", source, StringComparison.Ordinal);

        AssertCollectorClosureAstIsClean(observerClosure);
    }

    [Fact]
    public void CompiledCollectorBoundaryOwnsStartWaitTerminateReapAndDrain()
    {
        var collector = ReadFunction(ReadLifecycleGate(), "Invoke-OwnedDiagnosticCollector");

        Assert.Contains("DiagnosticCollectorRequest]::new", collector, StringComparison.Ordinal);
        Assert.Contains("OwnedDiagnosticCollector]::CollectAsync", collector, StringComparison.Ordinal);
        Assert.Contains("$CancellationToken", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.Diagnostics.Process]", collector, StringComparison.Ordinal);
        Assert.DoesNotContain(".Start(", collector, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("List[Exception]", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("AggregateException", collector, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSupervisorStartupBoundaryHasOwnedMonotonicEvidence()
    {
        var leaseSource = ReadRepositoryFile(
            "tools",
            "DownKyi.ProcessSupervision",
            "OwnedProcessLease.cs");
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    SupervisorStartupMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            leaseSource = leaseSource.Replace(
                "var useDirectAppHost = OperatingSystem.IsWindows();",
                "var useDirectAppHost = false;",
                StringComparison.Ordinal);
        }

        var collectorSource = ReadRepositoryFile(
            "tools",
            "DownKyi.ProcessSupervision",
            "OwnedDiagnosticCollector.cs");
        var contractSource = ReadRepositoryFile(
            "tools",
            "DownKyi.ProcessSupervision",
            "DiagnosticCollectorContracts.cs");
        var supervisorSource = ReadRepositoryFile(
            "tools",
            "DownKyi.ProcessSupervision",
            "SupervisorHost.cs");
        var regressionSource = ReadRepositoryFile(
            "tests",
            "ProcessSupervisionTestCases",
            "OwnedDiagnosticCollectorPlatformTests.cs");

        Assert.Contains(
            "var useDirectAppHost = OperatingSystem.IsWindows();",
            leaseSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Path.ChangeExtension(assemblyPath, \".exe\")",
            leaseSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnedProcessStartTimeline(budget)",
            collectorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ControlPipeConnectionCompleted",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "StatusPipeConnectionCompleted",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OperationDeadlineExhausted",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OperationDeadlineExhaustionObserved",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticCollectorOwnerJournal",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticCollectorFailureInterval",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "StartFailureTreeQuiescenceCompleted",
            contractSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "StallBeforeSupervisorPipeConnection",
            supervisorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SupervisorConnectionStallIdentifiesTheLastOwnedStartupTransition",
            regressionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnerJournalSurvivesPrimaryTimelineBlackoutAtStartupBoundary",
            regressionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellReportPreservesTypedCollectorFailureEvidence()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    DiagnosticProjectionMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            var mutation = TryExecuteLifecycleMutation(
                DiagnosticProjectionMutationEnvironmentVariable,
                "diagnostic-owner-projection",
                DiagnosticProjectionSelfTestRejection);
            if (mutation is null)
            {
                return;
            }

            Assert.False(
                IsExpectedLifecycleMutationRejection(
                    mutation,
                    DiagnosticProjectionSelfTestRejection),
                "The real lifecycle self-test rejected the old PowerShell nullable-member " +
                "projection semantics.");
            return;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable(CleanupReportMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            var mutation = TryExecuteLifecycleMutation(
                CleanupReportMutationEnvironmentVariable,
                "cleanup-report",
                CleanupReportSelfTestRejection);
            if (mutation is null)
            {
                return;
            }

            Assert.False(
                IsExpectedLifecycleMutationRejection(
                    mutation,
                    CleanupReportSelfTestRejection),
                "The real lifecycle self-test rejected lost collector cleanup evidence.");
            return;
        }

        var source = ReadLifecycleGate();
        var observer = ReadFunction(source, "Invoke-ForensicsObserverCapture");
        var isolatedProcess = ReadFunction(source, "Invoke-IsolatedProcess");
        var phaseResult = ReadFunction(source, "New-ProcessPhaseResult");
        var managedStack = ReadFunction(source, "Save-ManagedStack");
        var interruptedStackPolicy = ReadFunction(
            source,
            "Test-DiagnosticCollectorFailureHasCapturedStack");
        var localization = ReadFunction(
            source,
            "Get-DiagnosticCollectorStructuralLocalization");
        var persistenceSelfTest = ReadFunction(
            source,
            "Test-DiagnosticEvidencePersistenceFailureReport");

        Assert.Contains(
            "Get-DiagnosticCollectorExecutionFailure",
            observer,
            StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticCollectorExecutionException",
            ReadFunction(source, "Get-DiagnosticCollectorExecutionFailure"),
            StringComparison.Ordinal);
        Assert.Contains("collectorFailureKind", observer, StringComparison.Ordinal);
        Assert.Contains("collectorEvidence", observer, StringComparison.Ordinal);
        Assert.Contains("collectorOwnerJournal", observer, StringComparison.Ordinal);
        Assert.Contains("diagnosticLocalization", observer, StringComparison.Ordinal);
        Assert.Contains("evidenceCaptured", observer, StringComparison.Ordinal);
        Assert.Contains("evidencePersisted", observer, StringComparison.Ordinal);
        Assert.Contains("collectorCleanupFailures", observer, StringComparison.Ordinal);
        Assert.Contains(
            "ConvertTo-DiagnosticCollectorFailureReport",
            observer,
            StringComparison.Ordinal);
        Assert.Contains(
            "slowEvidenceCollectorFailureKind",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "exitEvidenceCollectorFailureKind",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "slowEvidenceCollectorCleanupFailures",
            phaseResult,
            StringComparison.Ordinal);
        Assert.Contains(
            "exitEvidenceCollectorCleanupFailures",
            phaseResult,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsCollectorCaptureWindowSelfTest =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsCollectorCleanupReportSelfTest =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsCollectorInterruptedStackSelfTestPassed =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsEvidencePersistenceSelfTestPassed =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-DiagnosticEvidencePersistenceFailureReport",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-DiagnosticEvidencePersistenceFailure",
            ReadFunction(source, "Save-ProcessEvidence"),
            StringComparison.Ordinal);
        Assert.Contains(
            "EvidencePersistenceFailure",
            localization,
            StringComparison.Ordinal);
        Assert.Contains("${OwnerJournal}?.FailureInterval", localization, StringComparison.Ordinal);
        Assert.Contains("${OwnerJournal}?.SupervisorProcessId", localization, StringComparison.Ordinal);
        Assert.Contains("${OwnerJournal}?.TargetProcessId", localization, StringComparison.Ordinal);
        Assert.DoesNotContain("$OwnerJournal?.FailureInterval", localization, StringComparison.Ordinal);
        Assert.DoesNotContain("$OwnerJournal?.SupervisorProcessId", localization, StringComparison.Ordinal);
        Assert.DoesNotContain("$OwnerJournal?.TargetProcessId", localization, StringComparison.Ordinal);
        Assert.Contains("OwnershipAcknowledgementFailure", persistenceSelfTest, StringComparison.Ordinal);
        Assert.Contains("collectorFailureKind", persistenceSelfTest, StringComparison.Ordinal);
        Assert.Contains("collectorCleanupFailures", persistenceSelfTest, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", persistenceSelfTest, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", persistenceSelfTest, StringComparison.Ordinal);
        Assert.Contains(
            "Test-DiagnosticCollectorFailureHasCapturedStack",
            managedStack,
            StringComparison.Ordinal);
        Assert.Contains("collectorFailureKind", managedStack, StringComparison.Ordinal);
        Assert.Contains("CallerCancelled", interruptedStackPolicy, StringComparison.Ordinal);
        Assert.Contains("CleanupFailures.Count -eq 0", interruptedStackPolicy, StringComparison.Ordinal);
        Assert.Contains("StreamsDrained", interruptedStackPolicy, StringComparison.Ordinal);
        Assert.Contains("StackOutputFirstByte", interruptedStackPolicy, StringComparison.Ordinal);
        Assert.Contains("^Thread", interruptedStackPolicy, StringComparison.Ordinal);
        Assert.Contains("emptyCancellationRejected", source, StringComparison.Ordinal);
        Assert.Contains("unrelatedFailureRejected", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticOwnerJournalSurvivesPrimaryEvidenceFailure()
    {
        var collector = ReadRepositoryFile(
            "tools",
            "DownKyi.ProcessSupervision",
            "OwnedDiagnosticCollector.cs");
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    DiagnosticJournalMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            collector = collector.Replace(
                "OwnerJournal = ownerJournal",
                "OwnerJournal = null",
                StringComparison.Ordinal);
        }

        var lifecycle = ReadLifecycleGate();
        var regression = ReadRepositoryFile(
            "tests",
            "ProcessSupervisionTestCases",
            "OwnedDiagnosticCollectorPlatformTests.cs");
        Assert.Contains("OwnerJournal = ownerJournal", collector, StringComparison.Ordinal);
        Assert.Contains("BuildOwnerJournal", collector, StringComparison.Ordinal);
        Assert.Contains("SuppressPrimaryTimeline", collector, StringComparison.Ordinal);
        Assert.Contains("collectorOwnerJournal", lifecycle, StringComparison.Ordinal);
        Assert.Contains("EvidencePersistenceFailure", lifecycle, StringComparison.Ordinal);
        Assert.Contains(
            "OwnerJournalSurvivesPrimaryTimelineBlackoutAtStartupBoundary",
            regression,
            StringComparison.Ordinal);
        Assert.Contains(
            "EvidenceCaptured",
            ReadFunction(lifecycle, "Test-DiagnosticEvidencePersistenceFailureReport"),
            StringComparison.Ordinal);
        Assert.Contains(
            "EvidencePersisted",
            ReadFunction(lifecycle, "Test-DiagnosticEvidencePersistenceFailureReport"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ForensicsCollectorsConsumeCallerAllocatedTypedWindows()
    {
        var source = ReadLifecycleGate();
        Assert.True(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(1, CaptureBudgetSelfTestRejection)));
        Assert.False(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(1, "Unrelated lifecycle failure.")));
        Assert.False(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(0, CaptureBudgetSelfTestRejection)));
        if (string.Equals(
                Environment.GetEnvironmentVariable(CaptureBudgetMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            var mutation = TryExecuteLifecycleMutation(
                CaptureBudgetMutationEnvironmentVariable,
                "capture-budget",
                CaptureBudgetSelfTestRejection);
            if (mutation is null)
            {
                return;
            }

            var expectedRejection = IsExpectedCaptureBudgetMutationRejection(mutation);
            Assert.False(
                expectedRejection,
                "The real lifecycle self-test emitted the exact rejection for the broken " +
                $"whole-budget collector window: {CaptureBudgetSelfTestRejection}" +
                $"{Environment.NewLine}Mutation child output:{Environment.NewLine}" +
                mutation.Output);
            return;
        }

        var isolatedProcess = ReadFunction(source, "Invoke-IsolatedProcess");
        var observerClosure = ReadFunctionClosure(source, "Invoke-ForensicsObserverCapture");
        var collector = ReadFunction(source, "Invoke-OwnedDiagnosticCollector");
        var delay = ReadFunction(source, "Wait-ForensicsObserverDelay");
        var snapshot = ReadFunction(source, "Get-DiagnosticProcessTreeSnapshot");
        var captureWindowSelfTest = ReadFunction(
            source,
            "Test-OwnedDiagnosticCollectorCaptureWindow");
        var attachStall = ReadFunction(source, "Test-DotnetStackAttachStall");

        Assert.Contains(
            "$forensicsCaptureWindowMilliseconds = 15000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsCaptureCleanupWindowMilliseconds = $processCleanupGraceSeconds * 1000",
            source,
            StringComparison.Ordinal);
        Assert.Equal(4, Regex.Count(source, "AllocateDiagnosticCollectorWindow"));
        Assert.Contains("AllocateDiagnosticCollectorWindow", isolatedProcess, StringComparison.Ordinal);
        foreach (var function in observerClosure.Values)
        {
            Assert.DoesNotContain("TransitionBudget]::Start", function, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "[System.Diagnostics.Stopwatch]::StartNew()",
                function,
                StringComparison.Ordinal);
        }
        Assert.Contains("[object]$CaptureWindow", collector, StringComparison.Ordinal);
        Assert.Contains("$CaptureWindow.DelayAsync", delay, StringComparison.Ordinal);
        Assert.Contains("$CaptureWindow.RemainingOperation", snapshot, StringComparison.Ordinal);
        Assert.Contains(
            "$typedOutcomeReturnedAtUnixMilliseconds -",
            attachStall,
            StringComparison.Ordinal);
        Assert.Contains(
            "$collectorRequestCreatedAtUnixMilliseconds) -ge 2900",
            attachStall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$typedOutcome.ElapsedMilliseconds -ge 2900",
            attachStall,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-OwnedDiagnosticCollectorCaptureWindow",
            source,
            StringComparison.Ordinal);
        Assert.Contains("--collector-block-with-ready", source, StringComparison.Ordinal);
        Assert.Contains("$collectorHostPath", captureWindowSelfTest, StringComparison.Ordinal);
        Assert.Contains(
            "-FileName $collectorHostPath",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-FileName \"dotnet\"",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Arguments @($ProcessSupervisionAssembly, \"--block-forever\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            CaptureBudgetMutationEnvironmentVariable,
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "collectorWindowOperationExhausted",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "$collectorRemainingOperation -eq [TimeSpan]::Zero",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "$parentRemainingOperation -gt [TimeSpan]::Zero",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "allocatedWindowWasAttenuated",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$budget.RemainingOperation -gt [TimeSpan]::FromSeconds(1)",
            captureWindowSelfTest,
            StringComparison.Ordinal);
        Assert.DoesNotContain("elapsedBounded", captureWindowSelfTest, StringComparison.Ordinal);
        Assert.DoesNotContain("New-OwnerAllocatedForensicsCaptureWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ForensicsCaptureWaitMilliseconds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureWindowSelfTestRejectsDeadlineBeforeFixtureReady()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(StartupWindowMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            var mutation = TryExecuteLifecycleMutation(
                StartupWindowMutationEnvironmentVariable,
                "startup-window",
                CaptureBudgetSelfTestRejection);
            if (mutation is null)
            {
                return;
            }

            Assert.False(
                IsExpectedLifecycleMutationRejection(
                    mutation,
                    CaptureBudgetSelfTestRejection),
                "The real lifecycle self-test rejected a deadline before fixture ready.");
            return;
        }

        var source = ReadFunction(
            ReadLifecycleGate(),
            "Test-OwnedDiagnosticCollectorCaptureWindow");
        Assert.Contains(StartupWindowMutationEnvironmentVariable, source, StringComparison.Ordinal);
        Assert.Contains("[TimeSpan]::FromMilliseconds(1)", source, StringComparison.Ordinal);
        Assert.Contains("blockingTaskEstablished", source, StringComparison.Ordinal);
        Assert.Contains("collectorStarted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureWindowSelfTestRejectsReadyPublishedBeforeBlockingTask()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(EarlyReadyMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            var mutation = TryExecuteLifecycleMutation(
                EarlyReadyMutationEnvironmentVariable,
                "early-ready",
                CaptureBudgetSelfTestRejection);
            if (mutation is null)
            {
                return;
            }

            Assert.False(
                IsExpectedLifecycleMutationRejection(
                    mutation,
                    CaptureBudgetSelfTestRejection),
                "The real lifecycle self-test rejected ready publication before blocking.");
            return;
        }

        var source = ReadFunction(
            ReadLifecycleGate(),
            "Test-OwnedDiagnosticCollectorCaptureWindow");
        Assert.Contains(EarlyReadyMutationEnvironmentVariable, source, StringComparison.Ordinal);
        Assert.Contains("--collector-publish-before-block", source, StringComparison.Ordinal);
        Assert.Contains("readyProcessIdValid", source, StringComparison.Ordinal);
        Assert.Contains("stdoutMarkerPreserved", source, StringComparison.Ordinal);
        Assert.Contains("stderrMarkerPreserved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceCaptureLeadStartsAfterTheOwnedLeaseIsEstablished()
    {
        var isolatedProcess = ReadFunction(ReadLifecycleGate(), "Invoke-IsolatedProcess");
        var leaseReadyIndex = isolatedProcess.IndexOf(
            "$processId = $lease.TargetProcessId",
            StringComparison.Ordinal);
        var observationClockIndex = isolatedProcess.IndexOf(
            "$evidenceObservationStopwatch = [System.Diagnostics.Stopwatch]::StartNew()",
            StringComparison.Ordinal);

        Assert.True(leaseReadyIndex >= 0);
        Assert.True(observationClockIndex > leaseReadyIndex);
        Assert.Contains(
            "$evidenceObservationStopwatch.Elapsed.TotalSeconds",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$stopwatch.Elapsed.TotalSeconds -ge $evidenceCaptureThresholdSeconds",
            isolatedProcess,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TargetExitCancelsOnlyTheObserverAndOwnsPhaseDuration()
    {
        var isolatedProcess = ReadFunction(ReadLifecycleGate(), "Invoke-IsolatedProcess");

        Assert.Contains("$lease.TargetExitedToken", isolatedProcess, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Count(isolatedProcess, @"-CancellationToken\s+\$observerCancellation\.Token"));
        Assert.Contains("$outcome.TargetExitedAfter", isolatedProcess, StringComparison.Ordinal);
        Assert.Contains(
            "$ownedFailure.Failure.TargetExitedAfter",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "durationMs = [Math]::Round($phaseDurationMs, 3)",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "durationMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "targetExitedAfterMilliseconds",
            ReadFunction(ReadLifecycleGate(), "New-ProcessPhaseResult"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverTruthMutationCannotSatisfyTheProcessOwnerGate()
    {
        var source = ReadLifecycleGate();
        if (string.Equals(
                Environment.GetEnvironmentVariable(MutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            source = source.Replace(
                "-not $ProcessResult.ownedTreeQuiescent -or",
                "(-not $ProcessResult.ownedTreeQuiescent -and " +
                "-not $ProcessResult.observerTreeQuiescent) -or",
                StringComparison.Ordinal);
        }

        var classifier = ReadFunction(source, "New-ProcessPhaseResult");
        Assert.Contains(
            "-not $ProcessResult.ownedTreeQuiescent -or",
            classifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$ProcessResult.observerTreeQuiescent",
            classifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$ProcessResult.diagnosticTreeQuiescent",
            classifier,
            StringComparison.Ordinal);
    }

    private static bool IsExpectedCaptureBudgetMutationRejection(
        BoundedProcessResult mutation)
    {
        return IsExpectedLifecycleMutationRejection(
            mutation,
            CaptureBudgetSelfTestRejection);
    }

    private static bool IsExpectedLifecycleMutationRejection(
        BoundedProcessResult mutation,
        string expectedMessage)
    {
        return mutation.ExitCode != 0 && mutation.Output.Contains(
            expectedMessage,
            StringComparison.Ordinal);
    }

    private static BoundedProcessResult? TryExecuteLifecycleMutation(
        string environmentVariable,
        string resultPrefix,
        string expectedMessage)
    {
        try
        {
            var result = ExecuteLifecycleMutation(environmentVariable, resultPrefix);
            if (!IsExpectedLifecycleMutationRejection(result, expectedMessage))
            {
                TestContext.Current.AddWarning(
                    $"Lifecycle {resultPrefix} mutation child result:" +
                    $"{Environment.NewLine}ExitCode: {result.ExitCode}" +
                    $"{Environment.NewLine}{result.Output}");
            }

            return result;
        }
        catch (Exception failure) when (failure is
            System.ComponentModel.Win32Exception or
            AggregateException or
            IOException or
            InvalidOperationException or
            OperationCanceledException or
            TimeoutException or
            UnauthorizedAccessException)
        {
            TestContext.Current.AddWarning(
                $"Lifecycle {resultPrefix} mutation host failure:" +
                $"{Environment.NewLine}{failure}");
            return null;
        }
    }

    private static BoundedProcessResult ExecuteLifecycleMutation(
        string environmentVariable,
        string resultPrefix)
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-forensics-{resultPrefix}-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(
                RepositoryRoot,
                "script",
                "test-assembly-lifecycle.ps1"));
            startInfo.ArgumentList.Add("-Configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-Profile");
            startInfo.ArgumentList.Add("Local");
            startInfo.ArgumentList.Add("-Iterations");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-AssemblyPattern");
            startInfo.ArgumentList.Add("DownKyi.Core.Tests");
            startInfo.ArgumentList.Add("-ResultsDirectory");
            startInfo.ArgumentList.Add(resultsDirectory);
            startInfo.ArgumentList.Add("-ValidateForensics");
            startInfo.ArgumentList.Add("-NoBuild");
            startInfo.Environment[environmentVariable] = "1";

            return BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken,
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            TryDeleteLifecycleMutationResults(resultsDirectory, resultPrefix);
        }
    }

    private static void TryDeleteLifecycleMutationResults(
        string resultsDirectory,
        string resultPrefix)
    {
        try
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            TestContext.Current.AddWarning(
                $"Lifecycle {resultPrefix} mutation cleanup failure:" +
                $"{Environment.NewLine}{failure}");
        }
    }

    private static void AssertCollectorClosureAstIsClean(
        IReadOnlyDictionary<string, string> closure)
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-collector-closure-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(temporaryPath, string.Join(Environment.NewLine, closure.Values));
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$Path=$env:DOWNKYI_COLLECTOR_AST_PATH; " +
                "$tokens=$null; $errors=$null; " +
                "$ast=[System.Management.Automation.Language.Parser]::ParseFile(" +
                "$Path,[ref]$tokens,[ref]$errors); " +
                "if($errors.Count -gt 0){$errors | ForEach-Object Message; exit 8}; " +
                "$legacy=@('New-OwnerAllocatedForensicsCaptureWindow'," +
                "'Get-ForensicsCaptureWaitMilliseconds','Invoke-BoundedForensicsCollector'," +
                "'Stop-BoundedForensicsCollector','Test-ForensicsCollectorCaptureWindow'); " +
                "$violations=[Collections.Generic.List[string]]::new(); " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.FunctionDefinitionAst]},$true) | " +
                "Where-Object Name -In $legacy | ForEach-Object {$violations.Add($_.Name)}; " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.InvokeMemberExpressionAst]},$true) | " +
                "ForEach-Object {$text=$_.Extent.Text; " +
                "if($text -match '(?i)\\[(System\\.)?Diagnostics\\.(ProcessStartInfo|Process)\\]" +
                "\\s*::\\s*(new|Start)' -or " +
                "$text -match '(?i)\\.(Kill|WaitForExit|WaitForExitAsync)\\s*\\(' -or " +
                "$text -match '(?i)Stopwatch\\]\\s*::\\s*StartNew' -or " +
                "$text -match '(?i)CancellationTokenSource\\]\\s*::' -or " +
                "$text -match '(?i)AggregateException\\]\\s*::\\s*new')" +
                "{$violations.Add($text)}}; " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.CommandAst]},$true) | " +
                "ForEach-Object {$name=$_.GetCommandName(); $text=$_.Extent.Text; " +
                "$leafName=if([string]::IsNullOrWhiteSpace($name)){$null}" +
                "else{($name -split '\\\\')[-1]}; " +
                "if($leafName -match '(?i)^(Start|Stop|Wait)-Process$' -or " +
                "($leafName -match '(?i)^New-Object$' -and " +
                "$text -match '(?i)(System\\.)?Diagnostics\\." +
                "(ProcessStartInfo|Process)')){$violations.Add($text)}}; " +
                "if($violations.Count -gt 0){$violations; exit 9}; exit 0");
            startInfo.Environment["DOWNKYI_COLLECTOR_AST_PATH"] = temporaryPath;

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken,
                TimeSpan.FromSeconds(15));
            Assert.True(
                result.ExitCode == 0,
                $"Collector closure contains forbidden PowerShell ownership AST: {result.Output}");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string ReadFunction(string source, string functionName)
    {
        var startToken = $"function {functionName} {{";
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Lifecycle function was not found: {functionName}");
        var next = source.IndexOf("\nfunction ", start + startToken.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static Dictionary<string, string> ReadFunctionClosure(
        string source,
        string rootFunction)
    {
        var functionNames = Regex.Matches(
                source,
                @"(?m)^function\s+(?<name>[A-Za-z0-9-]+)\s*\{")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        var closure = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(rootFunction);

        while (pending.Count > 0)
        {
            var functionName = pending.Dequeue();
            if (closure.ContainsKey(functionName))
            {
                continue;
            }

            var body = ReadFunction(source, functionName);
            closure.Add(functionName, body);
            foreach (var candidate in functionNames)
            {
                if (!closure.ContainsKey(candidate) &&
                    Regex.IsMatch(
                        body,
                        $@"(?<![A-Za-z0-9-]){Regex.Escape(candidate)}(?![A-Za-z0-9-])"))
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return closure;
    }

    private static string ReadLifecycleGate()
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "test-assembly-lifecycle.ps1"));
    }

    private static string ReadRepositoryFile(params string[] pathSegments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathSegments]));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DownKyi.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
