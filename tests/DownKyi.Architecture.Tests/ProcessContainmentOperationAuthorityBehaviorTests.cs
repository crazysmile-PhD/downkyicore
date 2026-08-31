using System.Reflection;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessContainmentOperationAuthorityBehaviorTests
{
    [Fact]
    public void CallerFactsRequireTheirBoundAuthoritativeObservation()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);

        Assert.Throws<InvalidOperationException>(() =>
            operation.Caller.PublishCancellation("caller canceled"));
        Assert.Throws<InvalidOperationException>(() =>
            operation.Caller.PublishDeadlineExceeded("caller deadline expired"));

        cancellation.Cancel();
        var canceled = operation.Caller.PublishCancellation("caller canceled");
        clock.Advance(TimeSpan.FromSeconds(10));
        var expired = operation.Caller.PublishDeadlineExceeded(
            "caller deadline expired");

        Assert.Equal(
            ProcessContainmentCallerFailureKind.Cancellation,
            canceled.Kind);
        Assert.Equal(
            ProcessContainmentCallerFailureKind.DeadlineExceeded,
            expired.Kind);
        Assert.Equal(nameof(OperationCanceledException), canceled.ErrorType);
        Assert.Equal(nameof(TimeoutException), expired.ErrorType);
    }

    [Fact]
    public async Task CapturedBackendExceptionRemainsBackendFailureAfterDelay()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        var continuation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResult = CaptureBackendFailureBeforeDelayAsync(
            operation.BackendResults,
            continuation.Task);

        clock.Advance(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.True(operation.Caller.Budget.Operation.IsExpired);
        Assert.True(operation.Caller.CancellationToken.IsCancellationRequested);
        continuation.SetResult();

        var rejected = AssertRejected(operation.FromBackend(
            await delayedResult.ConfigureAwait(true),
            []));

        Assert.IsAssignableFrom<ProcessContainmentBackendFailure>(
            rejected.PrimaryFailure);
        Assert.IsNotAssignableFrom<ProcessContainmentCallerFailure>(
            rejected.PrimaryFailure);
        Assert.IsNotAssignableFrom<ProcessContainmentContractFailure>(
            rejected.PrimaryFailure);
        Assert.Equal(
            nameof(InvalidOperationException),
            rejected.PrimaryFailure.ErrorType);
    }

    [Fact]
    public void RootAuthorityRejectsCallerBackendAndGuardSubstitution()
    {
        var clockA = new ManualMonotonicTimeProvider();
        var clockB = new ManualMonotonicTimeProvider();
        using var cancellationA = new CancellationTokenSource();
        using var cancellationB = new CancellationTokenSource();
        var operationA = Operation(clockA, cancellationA.Token);
        var operationB = Operation(clockB, cancellationB.Token);
        var reboundA = ProcessContainmentOperationAuthority.Create(
            operationA.Caller.Budget,
            operationA.Caller.CancellationToken);
        cancellationA.Cancel();
        cancellationB.Cancel();

        var ownCaller = operationA.Caller.PublishCancellation("caller A canceled");
        var foreignCaller = operationB.Caller.PublishCancellation("caller B canceled");
        var reboundCaller = reboundA.Caller.PublishCancellation("rebound A canceled");
        var ownResult = AssertRejected(operationA.Rejected(ownCaller, []));
        var foreignCallerResult = AssertRejected(
            operationA.Rejected(foreignCaller, []));
        var reboundCallerResult = AssertRejected(
            operationA.Rejected(reboundCaller, []));
        var foreignBackendResult = AssertRejected(operationA.FromBackend(
            operationB.BackendResults.Failed(
                new InvalidOperationException("fixture backend B failure"),
                "backend B failed"),
            []));
        var foreignGuardResult = AssertRejected(operationA.Rejected(
            operationB.ContractGuard.IllegalTransition("guard B failure"),
            []));

        Assert.Same(ownCaller, ownResult.PrimaryFailure);
        Assert.All(
        [
            foreignCallerResult,
            reboundCallerResult,
            foreignBackendResult,
            foreignGuardResult
        ],
            rejected =>
            {
                Assert.IsAssignableFrom<ProcessContainmentContractFailure>(
                    rejected.PrimaryFailure);
                Assert.Contains(
                    "does not own this operation lifetime",
                    rejected.PrimaryFailure.Detail,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void NonPublicConstructionCannotForgeCallerAuthority()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        var constructor = typeof(ProcessContainmentCallerFailure)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(
                [
                    typeof(object),
                    typeof(ProcessContainmentCallerFailureKind),
                    typeof(string)
                ]));
        var forged = Assert.IsType<ProcessContainmentCallerFailure>(
            constructor.Invoke(
            [
                new object(),
                ProcessContainmentCallerFailureKind.Cancellation,
                "reflected caller failure"
            ]));
        var foreignPublisher = new ProcessContainmentCallerFailure.Publisher();
        var foreignPublished = foreignPublisher.PublishCancellation(
            "foreign published caller failure");
        var invalidKind = Assert.Throws<TargetInvocationException>(() =>
            constructor.Invoke(
            [
                new object(),
                (ProcessContainmentCallerFailureKind)int.MaxValue,
                "invalid caller failure"
            ]));

        var rejected = AssertRejected(operation.Rejected(forged, []));
        var foreignRejected = AssertRejected(operation.Rejected(
            foreignPublished,
            []));

        Assert.All([rejected, foreignRejected], candidate =>
        {
            Assert.IsAssignableFrom<ProcessContainmentContractFailure>(
                candidate.PrimaryFailure);
            Assert.Contains(
                "does not own this operation lifetime",
                candidate.PrimaryFailure.Detail,
                StringComparison.Ordinal);
        });
        Assert.IsType<ArgumentOutOfRangeException>(invalidKind.InnerException);
    }

    [Fact]
    public void CleanupSnapshotCannotReplacePrimaryFailure()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        cancellation.Cancel();
        var primary = operation.Caller.PublishCancellation("caller canceled");
        var cleanup = new List<ProcessCleanupFailure>
        {
            Cleanup(
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                "resource release failed")
        };

        var rejected = AssertRejected(operation.Rejected(primary, cleanup));
        cleanup.Clear();
        cleanup.Add(Cleanup(
            ProcessCleanupFailureKind.StreamDrainFailure,
            "replacement cleanup failure"));

        Assert.Same(primary, rejected.PrimaryFailure);
        Assert.IsAssignableFrom<ProcessContainmentCallerFailure>(
            rejected.PrimaryFailure);
        Assert.Equal(
            ProcessCleanupFailureKind.ResourceReleaseFailure,
            Assert.Single(rejected.CleanupFailures).Kind);
    }

    [Fact]
    public void BackendFailurePublicationPreservesPrimaryAndCleanupSnapshot()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        var cleanup = new List<ProcessCleanupFailure>
        {
            Cleanup(
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                "backend cleanup failed")
        };

        var backendResult = operation.BackendResults.Failed(
                new IOException("fixture backend failure"),
                "backend failed");
        var publishedPrimary = Assert.IsAssignableFrom<
            ProcessContainmentBackendFailure>(backendResult.GetType()
                .GetProperty("Failure")!
                .GetValue(backendResult));
        var rejected = AssertRejected(operation.FromBackend(
            backendResult,
            cleanup));
        cleanup.Clear();
        cleanup.Add(Cleanup(
            ProcessCleanupFailureKind.StreamDrainFailure,
            "replacement cleanup failure"));

        Assert.Same(publishedPrimary, rejected.PrimaryFailure);
        Assert.Equal(nameof(IOException), publishedPrimary.ErrorType);
        Assert.Equal(
            ProcessCleanupFailureKind.ResourceReleaseFailure,
            Assert.Single(rejected.CleanupFailures).Kind);
    }

    [Fact]
    public void BackendSuccessAndGuardRejectionPreserveCleanupSnapshots()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        var foreignOperation = Operation(clock, cancellation.Token);
        var successCleanup = new List<ProcessCleanupFailure>
        {
            Cleanup(
                ProcessCleanupFailureKind.StreamDrainFailure,
                "success cleanup failed")
        };
        var guardCleanup = new List<ProcessCleanupFailure>
        {
            Cleanup(
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                "guard cleanup failed")
        };

        var completed = Assert.IsAssignableFrom<
            ProcessContainmentOperationCompleted>(operation.FromBackend(
                operation.BackendResults.Succeeded("backend evidence"),
                successCleanup));
        var guarded = AssertRejected(operation.FromBackend(
            foreignOperation.BackendResults.Succeeded("foreign evidence"),
            guardCleanup));
        successCleanup.Clear();
        guardCleanup.Clear();

        Assert.Equal("backend evidence", completed.Evidence);
        Assert.Equal(
            ProcessCleanupFailureKind.StreamDrainFailure,
            Assert.Single(completed.CleanupFailures).Kind);
        Assert.IsAssignableFrom<ProcessContainmentContractFailure>(
            guarded.PrimaryFailure);
        Assert.Equal(
            ProcessCleanupFailureKind.ResourceReleaseFailure,
            Assert.Single(guarded.CleanupFailures).Kind);
    }

    [Fact]
    public void FailureFamiliesCannotRepresentContradictoryAuthorityKinds()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        cancellation.Cancel();
        var caller = operation.Caller.PublishCancellation("caller canceled");
        var backend = AssertRejected(operation.FromBackend(
            operation.BackendResults.Failed(
                new IOException("fixture backend failure"),
                "backend failed"),
            [])).PrimaryFailure;
        var contract = operation.ContractGuard.IllegalTransition(
            "illegal transition");

        Assert.IsAssignableFrom<ProcessContainmentCallerFailure>(caller);
        Assert.IsNotAssignableFrom<ProcessContainmentBackendFailure>(caller);
        Assert.IsNotAssignableFrom<ProcessContainmentContractFailure>(caller);
        Assert.IsAssignableFrom<ProcessContainmentBackendFailure>(backend);
        Assert.IsNotAssignableFrom<ProcessContainmentCallerFailure>(backend);
        Assert.IsNotAssignableFrom<ProcessContainmentContractFailure>(backend);
        Assert.IsAssignableFrom<ProcessContainmentContractFailure>(contract);
        Assert.IsNotAssignableFrom<ProcessContainmentCallerFailure>(contract);
        Assert.IsNotAssignableFrom<ProcessContainmentBackendFailure>(contract);
    }

    [Fact]
    public async Task EnumerationAndSchedulerCannotMutatePublishedResult()
    {
        var clock = new ManualMonotonicTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Operation(clock, cancellation.Token);
        await cancellation.CancelAsync();
        var primary = operation.Caller.PublishCancellation("caller canceled");
        var cleanup = new List<ProcessCleanupFailure>
        {
            Cleanup(
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                "first cleanup failure"),
            Cleanup(
                ProcessCleanupFailureKind.StreamDrainFailure,
                "second cleanup failure")
        };
        var rejected = AssertRejected(operation.Rejected(primary, cleanup));

        cleanup.Reverse();
        cleanup.Clear();
        clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Yield();

        Assert.Same(primary, rejected.PrimaryFailure);
        Assert.Equal(
            [
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                ProcessCleanupFailureKind.StreamDrainFailure
            ],
            rejected.CleanupFailures.Select(failure => failure.Kind));
    }

    private static async Task<ProcessContainmentBackendResult>
        CaptureBackendFailureBeforeDelayAsync(
            ProcessContainmentBackendResultFactory factory,
            Task continuation)
    {
        ProcessContainmentBackendResult captured;
        try
        {
            throw new InvalidOperationException("fixture backend failure");
        }
        catch (InvalidOperationException failure)
        {
            captured = factory.Failed(failure, "backend failed");
        }

        await continuation.ConfigureAwait(false);
        return captured;
    }

    private static ProcessContainmentOperationRejected AssertRejected(
        ProcessContainmentOperationResult result)
    {
        return Assert.IsAssignableFrom<ProcessContainmentOperationRejected>(result);
    }

    private static ProcessContainmentOperationAuthority Operation(
        ManualMonotonicTimeProvider clock,
        CancellationToken cancellationToken)
    {
        var budget = TransitionBudget.StartForTesting(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            clock);
        return ProcessContainmentOperationAuthority.Create(
            budget,
            cancellationToken);
    }

    private static ProcessCleanupFailure Cleanup(
        ProcessCleanupFailureKind kind,
        string detail)
    {
        return new ProcessCleanupFailure(
            kind,
            nameof(InvalidOperationException),
            detail);
    }

    private sealed class ManualMonotonicTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            throw new InvalidOperationException(
                "Operation authority contracts must not read wall-clock time.");
        }

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            _timestamp = checked(_timestamp + elapsed.Ticks);
        }
    }
}
