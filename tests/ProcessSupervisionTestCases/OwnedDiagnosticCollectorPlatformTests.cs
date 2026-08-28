using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class OwnedDiagnosticCollectorPlatformTests
{
    [Fact]
    public async Task SuccessfulCollectorReturnsCompleteTypedEvidence()
    {
        var request = CreateRequest(
            SupervisorHost.CollectorOutputArgument,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));

        var outcome = await OwnedDiagnosticCollector.CollectAsync(
                request,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(outcome.Evidence.Started);
        Assert.True(outcome.Evidence.Exited);
        Assert.True(outcome.Evidence.Reaped);
        Assert.True(outcome.Evidence.StreamsDrained);
        Assert.False(outcome.Evidence.TimedOut);
        Assert.Equal(0, outcome.Evidence.ExitCode);
        Assert.Equal("collector-stdout", outcome.Evidence.StandardOutput);
        Assert.Equal("collector-stderr", outcome.Evidence.StandardError);
        Assert.Equal(11, outcome.Evidence.Timeline.Transitions.Count);
        Assert.Equal(
            DiagnosticCollectorTransitionState.NotObservable,
            GetTransition(outcome.Evidence, DiagnosticCollectorTransition.TargetAttachBegan).State);
        Assert.Equal(
            DiagnosticCollectorTransitionState.NotObservable,
            GetTransition(outcome.Evidence, DiagnosticCollectorTransition.StackCaptureBegan).State);
        Assert.All(
            new[]
            {
                DiagnosticCollectorTransition.RequestCreated,
                DiagnosticCollectorTransition.ProcessStartRequested,
                DiagnosticCollectorTransition.ProcessStarted,
                DiagnosticCollectorTransition.FirstObservableProgress,
                DiagnosticCollectorTransition.StackOutputFirstByte,
                DiagnosticCollectorTransition.ProcessExitObserved,
                DiagnosticCollectorTransition.ReapCompleted,
                DiagnosticCollectorTransition.StreamsDrained,
                DiagnosticCollectorTransition.TypedOutcomeReturned
            },
            transition => Assert.Equal(
                DiagnosticCollectorTransitionState.Observed,
                GetTransition(outcome.Evidence, transition).State));
    }

    [Fact]
    public async Task NonzeroExitIsACompletedCollectorOutcome()
    {
        var outcome = await OwnedDiagnosticCollector.CollectAsync(
                CreateRequest(
                    SupervisorHost.CollectorNonzeroArgument,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(2)),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(23, outcome.Evidence.ExitCode);
        Assert.True(outcome.Evidence.Reaped);
        Assert.True(outcome.Evidence.StreamsDrained);
        Assert.Equal("collector-stdout", outcome.Evidence.StandardOutput);
        Assert.Equal("collector-stderr", outcome.Evidence.StandardError);
    }

    [Fact]
    public async Task StartFailureDoesNotInventCleanupFailures()
    {
        var parent = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));
        var request = new DiagnosticCollectorRequest(
            new LaunchSpec(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
                Array.Empty<string>(),
                Path.GetTempPath()),
            parent.AllocateDiagnosticCollectorWindow(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));

        var failure = await Assert.ThrowsAsync<DiagnosticCollectorExecutionException>(
                () => OwnedDiagnosticCollector.CollectAsync(
                    request,
                    TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(DiagnosticCollectorFailureKind.StartFailed, failure.Failure.Kind);
        Assert.False(failure.Failure.Evidence.Started);
        Assert.Empty(failure.CleanupFailures);
        Assert.IsType<ReadOnlyCollection<DiagnosticCollectorCleanupFailure>>(
            failure.CleanupFailures);
    }

    [Fact]
    public async Task CancellationAfterStartFailureDoesNotReplaceTheCausalFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var parent = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        var request = new DiagnosticCollectorRequest(
            new LaunchSpec(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
                Array.Empty<string>(),
                Path.GetTempPath()),
            parent.AllocateDiagnosticCollectorWindow(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(300)));

        var failure = await Assert.ThrowsAsync<DiagnosticCollectorExecutionException>(
                () => OwnedDiagnosticCollector.CollectForTestingAsync(
                    request,
                    DiagnosticCollectorMutation.StallStreamDrain,
                    cancellation.Cancel,
                    cancellation.Token))
            .ConfigureAwait(true);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DiagnosticCollectorFailureKind.StartFailed, failure.Failure.Kind);
        Assert.IsNotType<OperationCanceledException>(failure.Failure.Cause);
        Assert.Contains(
            failure.CleanupFailures,
            item => item.Kind ==
                DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded);
    }

    [Fact]
    public async Task CancellationBeforeStartNeverLaunchesTheCollector()
    {
        var readyPath = CreateReadyPath("cancel-before-start");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);
        try
        {
            var failure = await Assert.ThrowsAsync<DiagnosticCollectorExecutionException>(
                    () => OwnedDiagnosticCollector.CollectAsync(
                        CreateRequest(
                            SupervisorHost.CollectorBlockWithReadyArgument,
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromSeconds(2),
                            readyPath),
                        cancellation.Token))
                .ConfigureAwait(true);

            Assert.Equal(DiagnosticCollectorFailureKind.CallerCancelled, failure.Failure.Kind);
            Assert.False(failure.Failure.Evidence.Started);
            Assert.False(File.Exists(readyPath));
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task CallerCancellationAfterStartCompletesBoundedCleanup()
    {
        var readyPath = CreateReadyPath("cancel-after-start");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var collection = OwnedDiagnosticCollector.CollectAsync(
                CreateRequest(
                    SupervisorHost.CollectorBlockWithReadyArgument,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(2),
                    readyPath),
                cancellation.Token);
            await WaitForReadyPathAsync(readyPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);

            var failure = await Assert.ThrowsAsync<DiagnosticCollectorExecutionException>(
                    () => collection)
                .ConfigureAwait(true);
            Assert.Equal(DiagnosticCollectorFailureKind.CallerCancelled, failure.Failure.Kind);
            Assert.True(failure.Failure.Evidence.Started);
            Assert.True(failure.Failure.Evidence.Reaped);
            Assert.True(failure.Failure.Evidence.StreamsDrained);
            Assert.Contains(
                "collector-before-block-stdout",
                failure.Failure.Evidence.StandardOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "collector-before-block-stderr",
                failure.Failure.Evidence.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task OperationDeadlineRemainsPrimaryAfterCleanCleanup()
    {
        var failure = await CollectBlockedFailureAsync(
                "operation-deadline",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                DiagnosticCollectorMutation.None)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.True(failure.Failure.Evidence.TimedOut);
        Assert.True(failure.Failure.Evidence.Reaped);
        Assert.True(failure.Failure.Evidence.StreamsDrained);
        Assert.Empty(failure.CleanupFailures);
        Assert.Equal(
            DiagnosticCollectorTransitionState.Observed,
            GetTransition(
                failure.Failure.Evidence,
                DiagnosticCollectorTransition.FirstObservableProgress).State);
    }

    [Fact]
    public async Task TargetExitCancellationStopsAnAlreadyStartedCollector()
    {
        var result = await RunTargetExitCancellationCaseAsync(
                useTargetExitCancellation: true,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        Assert.Equal(DiagnosticCollectorFailureKind.CallerCancelled, result.CollectorFailure.Kind);
        Assert.True(result.CollectorFailure.Evidence.Started);
        Assert.True(result.CollectorFailure.Evidence.Reaped);
        Assert.True(result.CollectorFailure.Evidence.StreamsDrained);
        Assert.Empty(result.CleanupFailures);
        Assert.True(result.TargetOutcome.TargetExitedAfter < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnlinkedTargetExitMutationConsumesTheCollectorWindow()
    {
        var result = await RunTargetExitCancellationCaseAsync(
                useTargetExitCancellation: false,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            result.CollectorFailure.Kind);
        Assert.True(result.CollectorFailure.Evidence.TimedOut);
        Assert.Equal(0, result.TargetOutcome.ExitCode);
        Assert.True(result.TargetOutcome.TargetExitedAfter < TimeSpan.FromSeconds(3));
        Assert.True(
            GetTransition(
                result.CollectorFailure.Evidence,
                DiagnosticCollectorTransition.TypedOutcomeReturned)
            .ElapsedMilliseconds >= 2500);
    }

    [Fact]
    public async Task DeadlineBeforeTargetStartDoesNotInventBlockedCollectorEvidence()
    {
        var readyPath = CreateReadyPath("deadline-before-start");
        try
        {
            var failure = await CollectFailureAsync(
                    CreateRequest(
                        SupervisorHost.CollectorBlockWithReadyArgument,
                        TimeSpan.FromTicks(1),
                        TimeSpan.FromSeconds(1),
                        readyPath),
                    DiagnosticCollectorMutation.None)
                .ConfigureAwait(true);

            Assert.Equal(
                DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
                failure.Failure.Kind);
            Assert.True(failure.Failure.Evidence.TimedOut);
            Assert.False(failure.Failure.Evidence.Started);
            Assert.False(failure.Failure.Evidence.Reaped);
            Assert.False(failure.Failure.Evidence.StreamsDrained);
            Assert.False(File.Exists(readyPath));
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task TerminateFailureDoesNotSkipReapOrDrain()
    {
        var failure = await CollectBlockedFailureAsync(
                "terminate-failure",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                DiagnosticCollectorMutation.FailAfterTerminate)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.Contains(
            failure.CleanupFailures,
            item => item.Kind == DiagnosticCollectorCleanupFailureKind.TerminateFailed);
        Assert.True(failure.Failure.Evidence.Reaped);
        Assert.True(failure.Failure.Evidence.StreamsDrained);
    }

    [Fact]
    public async Task ReapTimeoutIsTypedAndCannotBecomeSuccess()
    {
        var failure = await CollectBlockedFailureAsync(
                "reap-timeout",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(500),
                DiagnosticCollectorMutation.StallReap)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.Contains(
            failure.CleanupFailures,
            item => item.Kind ==
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded);
        Assert.False(failure.Failure.Evidence.Reaped);
    }

    [Fact]
    public async Task StreamDrainTimeoutIsAFirstClassFailure()
    {
        var failure = await CollectFailureAsync(
                CreateRequest(
                    SupervisorHost.CollectorOutputArgument,
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromMilliseconds(500)),
                DiagnosticCollectorMutation.StallStreamDrain)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.StreamDrainDeadlineExceeded,
            failure.Failure.Kind);
        Assert.True(failure.Failure.Evidence.Exited);
        Assert.True(failure.Failure.Evidence.Reaped);
        Assert.False(failure.Failure.Evidence.StreamsDrained);
        Assert.Contains(
            failure.CleanupFailures,
            item => item.Kind ==
                DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded);
    }

    [Fact]
    public async Task TimeoutAndCleanupFailuresPreserveCausalOrder()
    {
        var failure = await CollectBlockedFailureAsync(
                "causal-order",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                DiagnosticCollectorMutation.FailAfterTerminate)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.NotEmpty(failure.CleanupFailures);
        Assert.All(
            failure.CleanupFailures,
            item => Assert.Equal(
                DiagnosticCollectorCleanupFailureKind.TerminateFailed,
                item.Kind));
    }

    [Fact]
    public async Task LargeStdoutAndStderrDrainConcurrentlyWithoutMixing()
    {
        var outcome = await OwnedDiagnosticCollector.CollectAsync(
                CreateRequest(
                    SupervisorHost.CollectorLargeOutputArgument,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(2)),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(128 * 4096, outcome.Evidence.StandardOutput.Length);
        Assert.Equal(128 * 4096, outcome.Evidence.StandardError.Length);
        Assert.All(outcome.Evidence.StandardOutput, value => Assert.Equal('o', value));
        Assert.All(outcome.Evidence.StandardError, value => Assert.Equal('e', value));
        Assert.True(outcome.Evidence.StreamsDrained);
    }

    [Fact]
    public async Task ParentExitWithDescendantCannotReportCollectorCompletion()
    {
        var readyPath = CreateReadyPath("collector-descendant");
        try
        {
            var failure = await CollectFailureAsync(
                    CreateRequest(
                        SupervisorHost.ExitWithOwnedDescendantArgument,
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromSeconds(2),
                        readyPath),
                    DiagnosticCollectorMutation.None)
                .ConfigureAwait(true);

            Assert.True(File.Exists(readyPath));
            Assert.Equal(
                DiagnosticCollectorFailureKind.CollectorTreeNotQuiescent,
                failure.Failure.Kind);
            Assert.True(failure.Failure.Evidence.Exited);
            Assert.True(failure.Failure.Evidence.Reaped);
            Assert.True(failure.Failure.Evidence.StreamsDrained);
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task CollectorWindowCannotConsumeTheParentsRemainingOperationBudget()
    {
        var parent = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1));
        var failure = await CollectBlockedFailureAsync(
            parent,
            "parent-budget",
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(500),
            DiagnosticCollectorMutation.None)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.True(parent.RemainingOperation > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task IgnoringTheCallerAllocatedWindowFailsTheBehavioralProof()
    {
        var parent = TransitionBudget.Start(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(1));
        var failure = await CollectBlockedFailureAsync(
            parent,
            "ignored-window",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(500),
            DiagnosticCollectorMutation.IgnoreAllocatedWindow)
            .ConfigureAwait(true);

        Assert.Equal(
            DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            failure.Failure.Kind);
        Assert.Equal(TimeSpan.Zero, parent.RemainingOperation);
    }

    private static async Task<DiagnosticCollectorExecutionException> CollectFailureAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation)
    {
        return await Assert.ThrowsAsync<DiagnosticCollectorExecutionException>(
                () => OwnedDiagnosticCollector.CollectForTestingAsync(
                    request,
                    mutation,
                    TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    private static async Task<DiagnosticCollectorExecutionException> CollectBlockedFailureAsync(
        string name,
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance,
        DiagnosticCollectorMutation mutation)
    {
        var readyPath = CreateReadyPath(name);
        try
        {
            var failure = await CollectFailureAsync(
                    CreateRequest(
                        SupervisorHost.CollectorBlockWithReadyArgument,
                        operationAllowance,
                        cleanupAllowance,
                        readyPath),
                    mutation)
                .ConfigureAwait(true);
            AssertBlockingTaskEstablished(readyPath);
            return failure;
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    private static async Task<DiagnosticCollectorExecutionException> CollectBlockedFailureAsync(
        TransitionBudget parent,
        string name,
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance,
        DiagnosticCollectorMutation mutation)
    {
        var readyPath = CreateReadyPath(name);
        try
        {
            var failure = await CollectFailureAsync(
                    CreateRequest(
                        parent,
                        SupervisorHost.CollectorBlockWithReadyArgument,
                        operationAllowance,
                        cleanupAllowance,
                        readyPath),
                    mutation)
                .ConfigureAwait(true);
            AssertBlockingTaskEstablished(readyPath);
            return failure;
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    private static void AssertBlockingTaskEstablished(string readyPath)
    {
        Assert.True(File.Exists(readyPath));
        using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
        Assert.True(ready.RootElement.GetProperty("ProcessId").GetInt32() > 0);
        Assert.True(ready.RootElement.GetProperty("BlockingTaskEstablished").GetBoolean());
    }

    private static DiagnosticCollectorTransitionEvidence GetTransition(
        DiagnosticCollectorEvidence evidence,
        DiagnosticCollectorTransition transition)
    {
        return Assert.Single(
            evidence.Timeline.Transitions,
            item => item.Transition == transition);
    }

    private static async Task<TargetExitCancellationCaseResult>
        RunTargetExitCancellationCaseAsync(
            bool useTargetExitCancellation,
            TimeSpan collectorOperationAllowance)
    {
        var targetReadyPath = CreateReadyPath("target-exit-ready");
        var collectorReadyPath = CreateReadyPath("target-exit-signal");
        var assemblyPath = typeof(OwnedDiagnosticCollector).Assembly.Location;
        var targetBudget = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));
        OwnedProcessLease? targetLease = null;
        try
        {
            targetLease = await OwnedProcessLease.StartAsync(
                    new LaunchSpec(
                        "dotnet",
                        new[]
                        {
                            assemblyPath,
                            SupervisorHost.ExitOnFileSignalWithReadyArgument,
                            targetReadyPath,
                            collectorReadyPath
                        },
                        Path.GetDirectoryName(assemblyPath)
                            ?? throw new InvalidOperationException(
                                "The target-exit fixture directory is unavailable."),
                        closeStandardInput: true),
                    targetBudget,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await WaitForReadyPathAsync(
                    targetReadyPath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            var cancellationToken = useTargetExitCancellation
                ? targetLease.TargetExitedToken
                : CancellationToken.None;
            var collectorException = await Assert.ThrowsAsync<
                    DiagnosticCollectorExecutionException>(
                    () => OwnedDiagnosticCollector.CollectAsync(
                        CreateRequest(
                            SupervisorHost.CollectorBlockWithReadyArgument,
                            collectorOperationAllowance,
                            TimeSpan.FromSeconds(1),
                            collectorReadyPath),
                        cancellationToken))
                .ConfigureAwait(true);
            AssertBlockingTaskEstablished(collectorReadyPath);
            var targetOutcome = await targetLease.WaitAsync(
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            return new TargetExitCancellationCaseResult(
                collectorException.Failure,
                collectorException.CleanupFailures,
                targetOutcome);
        }
        finally
        {
            if (targetLease != null)
            {
                await targetLease.DisposeAsync().ConfigureAwait(true);
            }
            File.Delete(targetReadyPath);
            File.Delete(collectorReadyPath);
        }
    }

    private static DiagnosticCollectorRequest CreateRequest(
        string probeArgument,
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance,
        params string[] additionalArguments)
    {
        var parent = TransitionBudget.Start(
            operationAllowance + TimeSpan.FromSeconds(5),
            cleanupAllowance + TimeSpan.FromSeconds(1));
        return CreateRequest(
            parent,
            probeArgument,
            operationAllowance,
            cleanupAllowance,
            additionalArguments);
    }

    private static DiagnosticCollectorRequest CreateRequest(
        TransitionBudget parent,
        string probeArgument,
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance,
        params string[] additionalArguments)
    {
        var assemblyPath = typeof(OwnedDiagnosticCollector).Assembly.Location;
        var arguments = new[] { assemblyPath, probeArgument }
            .Concat(additionalArguments)
            .ToArray();
        return new DiagnosticCollectorRequest(
            new LaunchSpec(
                "dotnet",
                arguments,
                Path.GetDirectoryName(assemblyPath)
                    ?? throw new InvalidOperationException(
                        "The collector fixture directory is unavailable."),
                closeStandardInput: true),
            parent.AllocateDiagnosticCollectorWindow(
                operationAllowance,
                cleanupAllowance));
    }

    private static string CreateReadyPath(string name)
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"downkyi-{name}-{Guid.NewGuid():N}.json");
    }

    private static async Task WaitForReadyPathAsync(
        string readyPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(readyPath)
            ?? throw new InvalidOperationException(
                "The collector fixture directory is unavailable.");
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(readyPath));
        FileSystemEventHandler created = (_, _) => completion.TrySetResult();
        RenamedEventHandler renamed = (_, _) => completion.TrySetResult();
        watcher.Created += created;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(readyPath))
        {
            completion.TrySetResult();
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record TargetExitCancellationCaseResult(
        DiagnosticCollectorFailure CollectorFailure,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures,
        OwnedProcessOutcome TargetOutcome);
}

public sealed class DiagnosticCollectorWindowTests
{
    [Fact]
    public void CollectorWindowSharesAndAttenuatesTheParentTimeline()
    {
        var timeProvider = new ManualTimeProvider();
        var parent = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2),
            timeProvider);
        var window = parent.AllocateDiagnosticCollectorWindow(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), window.RemainingOperation);
        Assert.Equal(TimeSpan.FromSeconds(2), window.RemainingCleanup);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, window.RemainingOperation);
        Assert.Equal(TimeSpan.Zero, window.RemainingCleanup);
        Assert.Equal(TimeSpan.FromSeconds(2), parent.RemainingOperation);
        Assert.Equal(TimeSpan.FromSeconds(4), parent.RemainingCleanup);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        }

        public void Advance(TimeSpan duration)
        {
            _timestamp = checked(_timestamp + duration.Ticks);
        }
    }
}
