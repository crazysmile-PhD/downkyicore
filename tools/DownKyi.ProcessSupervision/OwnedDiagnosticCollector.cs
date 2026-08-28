using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports the collector API to PowerShell.

public static class OwnedDiagnosticCollector
{
    public static Task<DiagnosticCollectorOutcome> CollectAsync(
        DiagnosticCollectorRequest request,
        CancellationToken cancellationToken = default)
    {
        return CollectCoreAsync(
            request,
            DiagnosticCollectorMutation.None,
            startFailureObservedForTesting: null,
            collectorStartedForTesting: null,
            cancellationToken);
    }

    internal static Task<DiagnosticCollectorOutcome> CollectForTestingAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation,
        CancellationToken cancellationToken = default)
    {
        return CollectCoreAsync(
            request,
            mutation,
            startFailureObservedForTesting: null,
            collectorStartedForTesting: null,
            cancellationToken);
    }

    internal static Task<DiagnosticCollectorOutcome> CollectForTestingAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation,
        Action startFailureObservedForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startFailureObservedForTesting);
        return CollectCoreAsync(
            request,
            mutation,
            startFailureObservedForTesting,
            collectorStartedForTesting: null,
            cancellationToken);
    }

    internal static Task<DiagnosticCollectorOutcome> CollectWithStartedObservationForTestingAsync(
        DiagnosticCollectorRequest request,
        TaskCompletionSource collectorStartedForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collectorStartedForTesting);
        return CollectCoreAsync(
            request,
            DiagnosticCollectorMutation.None,
            startFailureObservedForTesting: null,
            collectorStartedForTesting,
            cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The collector boundary must convert every start and ownership failure into its typed public contract.")]
    private static async Task<DiagnosticCollectorOutcome> CollectCoreAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation,
        Action? startFailureObservedForTesting,
        TaskCompletionSource? collectorStartedForTesting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeline = new DiagnosticCollectorTimelineBuilder(request);
        if (cancellationToken.IsCancellationRequested)
        {
            timeline.MarkTypedOutcomeReturned();
            throw CreateFailure(
                DiagnosticCollectorFailureKind.CallerCancelled,
                CreateNotStartedEvidence(timedOut: false, timeline.Build()),
                new OperationCanceledException(cancellationToken),
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }
        if (request.Window.RemainingOperation <= TimeSpan.Zero)
        {
            timeline.MarkTypedOutcomeReturned();
            throw CreateFailure(
                DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
                CreateNotStartedEvidence(timedOut: true, timeline.Build()),
                new TimeoutException(
                    "The diagnostic collector window was exhausted before launch."),
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }

        var budget = mutation.HasFlag(DiagnosticCollectorMutation.IgnoreAllocatedWindow)
            ? request.Window.ParentBudget
            : request.Window.Budget;
        var processMutation = MapMutation(mutation);
        OwnedProcessLease lease;
        try
        {
            timeline.Mark(DiagnosticCollectorTransition.ProcessStartRequested);
            lease = startFailureObservedForTesting != null
                ? await OwnedProcessLease.StartForTestingAsync(
                        request.Launch,
                        budget,
                        processMutation,
                        startFailureObservedForTesting,
                        cancellationToken)
                    .ConfigureAwait(false)
                : processMutation == ProcessOwnershipMutation.None
                ? await OwnedProcessLease.StartAsync(
                        request.Launch,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await OwnedProcessLease.StartForTestingAsync(
                        request.Launch,
                        budget,
                        processMutation,
                        cancellationToken)
                    .ConfigureAwait(false);
            timeline.Mark(DiagnosticCollectorTransition.ProcessStarted);
            collectorStartedForTesting?.TrySetResult();
        }
        catch (Exception failure)
        {
            var (primary, cleanupFailures) = SplitStartFailure(failure);
            var kind = primary is OperationCanceledException
                ? DiagnosticCollectorFailureKind.CallerCancelled
                : primary is TimeoutException
                    ? DiagnosticCollectorFailureKind.OperationDeadlineExceeded
                    : DiagnosticCollectorFailureKind.StartFailed;
            timeline.MarkTypedOutcomeReturned();
            throw CreateFailure(
                kind,
                CreateNotStartedEvidence(
                    timedOut: kind == DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
                    timeline.Build()),
                primary,
                cleanupFailures);
        }

        await using var leaseScope = lease.ConfigureAwait(false);
        try
        {
            var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
            timeline.ObserveOwnedProcess(lease, outcome.TargetExitedAfter);
            timeline.MarkTypedOutcomeReturned();
            return new DiagnosticCollectorOutcome(
                new DiagnosticCollectorEvidence(
                    Started: true,
                    Exited: true,
                    Reaped: true,
                    StreamsDrained: true,
                    TimedOut: false,
                    outcome.ExitCode,
                    outcome.StandardOutput,
                    outcome.StandardError,
                    timeline.Build()));
        }
        catch (OwnedProcessExecutionException failure)
        {
            var cleanupFailures = MapCleanupFailures(failure.CleanupStageFailures);
            var kind = MapFailureKind(failure.Failure.Kind);
            var reapFailed = cleanupFailures.Any(item => item.Kind is
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded or
                DiagnosticCollectorCleanupFailureKind.ReapFailed);
            var drainFailed = cleanupFailures.Any(item =>
                item.Kind == DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded);
            timeline.ObserveOwnedProcess(lease, failure.Failure.TargetExitedAfter);
            timeline.MarkTypedOutcomeReturned();
            var evidence = new DiagnosticCollectorEvidence(
                Started: true,
                Exited: failure.Failure.TargetExitedAtUnixMilliseconds.HasValue || !reapFailed,
                Reaped: !reapFailed,
                StreamsDrained:
                    failure.Failure.Kind != OwnedProcessFailureKind.StreamDrainDeadlineExceeded &&
                    !drainFailed,
                TimedOut: kind is
                    DiagnosticCollectorFailureKind.OperationDeadlineExceeded or
                    DiagnosticCollectorFailureKind.StreamDrainDeadlineExceeded,
                ExitCode: null,
                failure.Failure.StandardOutput,
                failure.Failure.StandardError,
                timeline.Build());
            throw CreateFailure(
                kind,
                evidence,
                failure.InnerException ?? failure,
                cleanupFailures);
        }
        catch (Exception failure)
        {
            var cleanupFailures = new[]
            {
                new DiagnosticCollectorCleanupFailure(
                    DiagnosticCollectorCleanupFailureKind.DisposeFailed,
                    failure)
            };
            timeline.ObserveOwnedProcess(lease, targetExitedAfter: null);
            timeline.MarkTypedOutcomeReturned();
            throw CreateFailure(
                DiagnosticCollectorFailureKind.CleanupFailed,
                new DiagnosticCollectorEvidence(
                    Started: true,
                    Exited: false,
                    Reaped: false,
                    StreamsDrained: false,
                    TimedOut: false,
                    ExitCode: null,
                    StandardOutput: string.Empty,
                    StandardError: string.Empty,
                    timeline.Build()),
                failure,
                cleanupFailures);
        }
    }

    private static ProcessOwnershipMutation MapMutation(DiagnosticCollectorMutation mutation)
    {
        var processMutation = ProcessOwnershipMutation.None;
        if (mutation.HasFlag(DiagnosticCollectorMutation.FailAfterTerminate))
        {
            processMutation |= ProcessOwnershipMutation.FailAfterContainmentTermination;
        }
        if (mutation.HasFlag(DiagnosticCollectorMutation.StallReap))
        {
            processMutation |= ProcessOwnershipMutation.StallRootReap;
        }
        if (mutation.HasFlag(DiagnosticCollectorMutation.StallStreamDrain))
        {
            processMutation |= ProcessOwnershipMutation.StallStreamDrain;
        }

        return processMutation;
    }

    private static DiagnosticCollectorFailureKind MapFailureKind(
        OwnedProcessFailureKind kind)
    {
        return kind switch
        {
            OwnedProcessFailureKind.OperationDeadlineExceeded =>
                DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            OwnedProcessFailureKind.OwnedTreeNotQuiescent =>
                DiagnosticCollectorFailureKind.CollectorTreeNotQuiescent,
            OwnedProcessFailureKind.StreamDrainDeadlineExceeded =>
                DiagnosticCollectorFailureKind.StreamDrainDeadlineExceeded,
            OwnedProcessFailureKind.CallerCancelled =>
                DiagnosticCollectorFailureKind.CallerCancelled,
            _ => DiagnosticCollectorFailureKind.ExecutionFailed
        };
    }

    private static ReadOnlyCollection<DiagnosticCollectorCleanupFailure> MapCleanupFailures(
        IEnumerable<OwnedProcessCleanupStageFailure> failures)
    {
        return new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(
            failures.Select(MapCleanupFailure).ToArray());
    }

    private static DiagnosticCollectorCleanupFailure MapCleanupFailure(
        OwnedProcessCleanupStageFailure failure)
    {
        var kind = failure.Stage switch
        {
            OwnedProcessCleanupStage.Terminate =>
                DiagnosticCollectorCleanupFailureKind.TerminateFailed,
            OwnedProcessCleanupStage.TreeQuiescence =>
                DiagnosticCollectorCleanupFailureKind.CollectorTreeNotQuiescent,
            OwnedProcessCleanupStage.Reap when failure.Cause is TimeoutException =>
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded,
            OwnedProcessCleanupStage.Reap =>
                DiagnosticCollectorCleanupFailureKind.ReapFailed,
            OwnedProcessCleanupStage.StreamDrain =>
                DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded,
            _ => DiagnosticCollectorCleanupFailureKind.DisposeFailed
        };
        return new DiagnosticCollectorCleanupFailure(kind, failure.Cause);
    }

    private static (
        Exception Primary,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures)
        SplitStartFailure(Exception failure)
    {
        if (failure is not AggregateException aggregate ||
            aggregate.InnerExceptions.Count == 0)
        {
            return (
                failure,
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }

        var primary = aggregate.InnerExceptions[0];
        var cleanup = aggregate.InnerExceptions
            .Skip(1)
            .Select(OwnedProcessCleanupStageFailure.FromException)
            .Select(MapCleanupFailure)
            .ToArray();
        return (
            primary,
            new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(cleanup));
    }

    private static DiagnosticCollectorEvidence CreateNotStartedEvidence(
        bool timedOut,
        DiagnosticCollectorTimeline timeline)
    {
        return new DiagnosticCollectorEvidence(
            Started: false,
            Exited: false,
            Reaped: false,
            StreamsDrained: false,
            TimedOut: timedOut,
            ExitCode: null,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            timeline);
    }

    private static DiagnosticCollectorExecutionException CreateFailure(
        DiagnosticCollectorFailureKind kind,
        DiagnosticCollectorEvidence evidence,
        Exception cause,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
    {
        return new DiagnosticCollectorExecutionException(
            new DiagnosticCollectorFailure(kind, evidence, cause),
            cleanupFailures);
    }

    private sealed class DiagnosticCollectorTimelineBuilder
    {
        private readonly DiagnosticCollectorRequest _request;
        private readonly Dictionary<DiagnosticCollectorTransition, DiagnosticCollectorTransitionEvidence>
            _transitions = new();

        public DiagnosticCollectorTimelineBuilder(DiagnosticCollectorRequest request)
        {
            _request = request;
            Mark(
                DiagnosticCollectorTransition.RequestCreated,
                request.CreatedAfterWindowStart,
                "The caller created the immutable collector request.");
        }

        public void Mark(
            DiagnosticCollectorTransition transition,
            string? detail = null)
        {
            Mark(transition, _request.Window.Budget.Elapsed, detail);
        }

        public void ObserveOwnedProcess(
            OwnedProcessLease lease,
            TimeSpan? targetExitedAfter)
        {
            ArgumentNullException.ThrowIfNull(lease);
            MarkNotObservable(
                DiagnosticCollectorTransition.TargetAttachBegan,
                "The generic collector owner cannot observe an external tool's attach boundary.");
            MarkNotObservable(
                DiagnosticCollectorTransition.StackCaptureBegan,
                "The generic collector owner cannot observe an external tool's capture boundary.");

            var firstProgress = Minimum(
                lease.StandardOutputFirstObservedAfter,
                lease.StandardErrorFirstObservedAfter);
            if (firstProgress.HasValue)
            {
                Mark(
                    DiagnosticCollectorTransition.FirstObservableProgress,
                    firstProgress.Value,
                    "The owner observed the first stdout or stderr data.");
            }

            if (lease.StandardOutputFirstObservedAfter.HasValue)
            {
                Mark(
                    DiagnosticCollectorTransition.StackOutputFirstByte,
                    lease.StandardOutputFirstObservedAfter.Value,
                    "The owner observed the first stdout data.");
            }

            var observedTargetExitedAfter =
                targetExitedAfter ?? lease.ObservedTargetExitedAfter;
            if (observedTargetExitedAfter.HasValue)
            {
                Mark(
                    DiagnosticCollectorTransition.ProcessExitObserved,
                    observedTargetExitedAfter.Value,
                    "The owned collector process reported target exit.");
            }

            if (lease.ReapedAfter.HasValue)
            {
                Mark(
                    DiagnosticCollectorTransition.ReapCompleted,
                    lease.ReapedAfter.Value,
                    "The owned collector root was authoritatively reaped.");
            }

            var streamsDrained = Maximum(
                lease.StandardOutputDrainedAfter,
                lease.StandardErrorDrainedAfter);
            if (streamsDrained.HasValue)
            {
                Mark(
                    DiagnosticCollectorTransition.StreamsDrained,
                    streamsDrained.Value,
                    "Both owned collector streams reached EOF.");
            }
        }

        public void MarkTypedOutcomeReturned()
        {
            Mark(
                DiagnosticCollectorTransition.TypedOutcomeReturned,
                "The collector boundary returned or threw its typed result.");
        }

        public DiagnosticCollectorTimeline Build()
        {
            if (_transitions.ContainsKey(DiagnosticCollectorTransition.ProcessStarted))
            {
                MarkNotObservable(
                    DiagnosticCollectorTransition.TargetAttachBegan,
                    "The generic collector owner cannot observe an external tool's attach boundary.");
                MarkNotObservable(
                    DiagnosticCollectorTransition.StackCaptureBegan,
                    "The generic collector owner cannot observe an external tool's capture boundary.");
            }

            var entries = Enum.GetValues<DiagnosticCollectorTransition>()
                .Select(transition => _transitions.TryGetValue(transition, out var evidence)
                    ? evidence
                    : new DiagnosticCollectorTransitionEvidence(
                        transition,
                        DiagnosticCollectorTransitionState.NotObserved,
                        ElapsedMilliseconds: null,
                        Detail: "The transition was not observed before the typed result."))
                .ToArray();
            return new DiagnosticCollectorTimeline(
                new ReadOnlyCollection<DiagnosticCollectorTransitionEvidence>(entries));
        }

        private void Mark(
            DiagnosticCollectorTransition transition,
            TimeSpan elapsedAfterWindowStart,
            string? detail)
        {
            var elapsed = elapsedAfterWindowStart - _request.CreatedAfterWindowStart;
            _transitions[transition] = new DiagnosticCollectorTransitionEvidence(
                transition,
                DiagnosticCollectorTransitionState.Observed,
                Math.Round(Math.Max(0, elapsed.TotalMilliseconds), 3),
                detail);
        }

        private void MarkNotObservable(
            DiagnosticCollectorTransition transition,
            string detail)
        {
            _transitions.TryAdd(
                transition,
                new DiagnosticCollectorTransitionEvidence(
                    transition,
                    DiagnosticCollectorTransitionState.NotObservable,
                    ElapsedMilliseconds: null,
                    detail));
        }

        private static TimeSpan? Minimum(TimeSpan? left, TimeSpan? right)
        {
            if (!left.HasValue)
            {
                return right;
            }
            if (!right.HasValue)
            {
                return left;
            }
            return left.Value <= right.Value ? left : right;
        }

        private static TimeSpan? Maximum(TimeSpan? left, TimeSpan? right)
        {
            if (!left.HasValue || !right.HasValue)
            {
                return null;
            }
            return left.Value >= right.Value ? left : right;
        }
    }
}
