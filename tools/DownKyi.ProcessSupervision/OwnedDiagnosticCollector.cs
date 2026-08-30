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
        timeline.Mark(DiagnosticCollectorTransition.CollectorDispatchRequested);
        if (cancellationToken.IsCancellationRequested)
        {
            timeline.MarkTypedOutcomeReturned();
            var kind = DiagnosticCollectorFailureKind.CallerCancelled;
            throw CreateFailure(
                kind,
                CreateNotStartedEvidence(
                    timedOut: false,
                    timeline.BuildPrimaryTimeline(mutation)),
                new OperationCanceledException(cancellationToken),
                Array.Empty<DiagnosticCollectorCleanupFailure>(),
                timeline.BuildOwnerJournal(
                    kind,
                    Array.Empty<DiagnosticCollectorCleanupFailure>()));
        }
        if (request.Window.RemainingOperation <= TimeSpan.Zero)
        {
            timeline.MarkTypedOutcomeReturned();
            var kind = DiagnosticCollectorFailureKind.OperationDeadlineExceeded;
            throw CreateFailure(
                kind,
                CreateNotStartedEvidence(
                    timedOut: true,
                    timeline.BuildPrimaryTimeline(mutation)),
                new TimeoutException(
                    "The diagnostic collector window was exhausted before launch."),
                Array.Empty<DiagnosticCollectorCleanupFailure>(),
                timeline.BuildOwnerJournal(
                    kind,
                    Array.Empty<DiagnosticCollectorCleanupFailure>()));
        }

        var budget = mutation.HasFlag(DiagnosticCollectorMutation.IgnoreAllocatedWindow)
            ? request.Window.ParentBudget
            : request.Window.Budget;
        var processMutation = MapMutation(mutation);
        var processStartTimeline = new OwnedProcessStartTimeline(budget);
        OwnedProcessLease lease;
        try
        {
            timeline.Mark(DiagnosticCollectorTransition.ProcessStartRequested);
            lease = await OwnedProcessLease.StartObservedAsync(
                    request.Launch,
                    budget,
                    processMutation,
                    processStartTimeline,
                    startFailureObservedForTesting,
                    cancellationToken)
                .ConfigureAwait(false);
            timeline.ObserveOwnedProcessStart(processStartTimeline);
            timeline.Mark(DiagnosticCollectorTransition.ProcessStarted);
            collectorStartedForTesting?.TrySetResult();
        }
        catch (Exception failure)
        {
            timeline.ObserveOwnedProcessStart(processStartTimeline);
            var (primary, cleanupFailures) = SplitStartFailure(failure);
            var kind = primary is OperationCanceledException
                ? DiagnosticCollectorFailureKind.CallerCancelled
                : primary is TimeoutException
                    ? DiagnosticCollectorFailureKind.OperationDeadlineExceeded
                    : DiagnosticCollectorFailureKind.StartFailed;
            timeline.RecordFailureInterval();
            timeline.MarkTypedOutcomeReturned();
            throw CreateFailure(
                kind,
                CreateNotStartedEvidence(
                    timedOut: kind == DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
                    timeline.BuildPrimaryTimeline(mutation)),
                primary,
                cleanupFailures,
                timeline.BuildOwnerJournal(kind, cleanupFailures));
        }

        await using var leaseScope = lease.ConfigureAwait(false);
        try
        {
            var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
            timeline.ObserveOwnedProcess(lease, outcome.TargetExitedAfter);
            timeline.MarkTypedOutcomeReturned();
            var ownerJournal = timeline.BuildOwnerJournal(
                failureKind: null,
                Array.Empty<DiagnosticCollectorCleanupFailure>());
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
                    timeline.BuildPrimaryTimeline(mutation)))
            {
                OwnerJournal = ownerJournal
            };
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
            timeline.ObserveOwnedProcessProgress(lease);
            if (kind == DiagnosticCollectorFailureKind.OperationDeadlineExceeded)
            {
                timeline.MarkOperationDeadlineExhausted();
            }
            timeline.RecordFailureInterval();
            timeline.ObserveOwnedProcessSettlement(
                lease,
                failure.Failure.TargetExitedAfter);
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
                timeline.BuildPrimaryTimeline(mutation));
            throw CreateFailure(
                kind,
                evidence,
                failure.InnerException ?? failure,
                cleanupFailures,
                timeline.BuildOwnerJournal(kind, cleanupFailures));
        }
        catch (Exception failure)
        {
            var cleanupFailures = new[]
            {
                new DiagnosticCollectorCleanupFailure(
                    DiagnosticCollectorCleanupFailureKind.DisposeFailed,
                    failure)
            };
            timeline.ObserveOwnedProcessProgress(lease);
            timeline.RecordFailureInterval();
            timeline.ObserveOwnedProcessSettlement(lease, targetExitedAfter: null);
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
                    timeline.BuildPrimaryTimeline(mutation)),
                failure,
                cleanupFailures,
                timeline.BuildOwnerJournal(
                    DiagnosticCollectorFailureKind.CleanupFailed,
                    cleanupFailures));
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
        if (mutation.HasFlag(DiagnosticCollectorMutation.StallBeforeSupervisorPipeConnection))
        {
            processMutation |= ProcessOwnershipMutation.StallBeforeSupervisorPipeConnection;
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
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures,
        DiagnosticCollectorOwnerJournal ownerJournal)
    {
        return new DiagnosticCollectorExecutionException(
            new DiagnosticCollectorFailure(kind, evidence, cause)
            {
                OwnerJournal = ownerJournal
            },
            cleanupFailures);
    }

    private sealed class DiagnosticCollectorTimelineBuilder
    {
        private static readonly DiagnosticCollectorTransition[] OrderedTransitions =
        [
            DiagnosticCollectorTransition.RequestCreated,
            DiagnosticCollectorTransition.CollectorDispatchRequested,
            DiagnosticCollectorTransition.ProcessStartRequested,
            DiagnosticCollectorTransition.SupervisorProcessStartReturned,
            DiagnosticCollectorTransition.ContainmentPrepared,
            DiagnosticCollectorTransition.ContainmentEstablished,
            DiagnosticCollectorTransition.ControlPipeConnectionCompleted,
            DiagnosticCollectorTransition.StatusPipeConnectionCompleted,
            DiagnosticCollectorTransition.OwnershipAcknowledgementReceived,
            DiagnosticCollectorTransition.LaunchAuthorizationWritten,
            DiagnosticCollectorTransition.TargetStartAcknowledgementReceived,
            DiagnosticCollectorTransition.ProcessStarted,
            DiagnosticCollectorTransition.StartFailureObserved,
            DiagnosticCollectorTransition.OperationDeadlineExhausted,
            DiagnosticCollectorTransition.OperationDeadlineExhaustionObserved,
            DiagnosticCollectorTransition.StartFailureTerminationBegan,
            DiagnosticCollectorTransition.StartFailureTerminationCompleted,
            DiagnosticCollectorTransition.StartFailureTreeQuiescenceBegan,
            DiagnosticCollectorTransition.StartFailureTreeQuiescenceCompleted,
            DiagnosticCollectorTransition.StartFailureSupervisorReapBegan,
            DiagnosticCollectorTransition.StartFailureSupervisorReapCompleted,
            DiagnosticCollectorTransition.StartFailureStreamDrainBegan,
            DiagnosticCollectorTransition.StartFailureStreamDrainCompleted,
            DiagnosticCollectorTransition.FailureTerminationBegan,
            DiagnosticCollectorTransition.FailureTerminationCompleted,
            DiagnosticCollectorTransition.FailureTreeQuiescenceBegan,
            DiagnosticCollectorTransition.FailureTreeQuiescenceCompleted,
            DiagnosticCollectorTransition.FailureSupervisorReapBegan,
            DiagnosticCollectorTransition.FailureSupervisorReapCompleted,
            DiagnosticCollectorTransition.FailureStreamDrainBegan,
            DiagnosticCollectorTransition.FailureStreamDrainCompleted,
            DiagnosticCollectorTransition.TargetAttachBegan,
            DiagnosticCollectorTransition.FirstObservableProgress,
            DiagnosticCollectorTransition.StackCaptureBegan,
            DiagnosticCollectorTransition.StackOutputFirstByte,
            DiagnosticCollectorTransition.ProcessExitObserved,
            DiagnosticCollectorTransition.ReapCompleted,
            DiagnosticCollectorTransition.StreamsDrained,
            DiagnosticCollectorTransition.TypedOutcomeReturned
        ];
        private static readonly DiagnosticCollectorTransition[] RequiredStartupTransitions =
        [
            DiagnosticCollectorTransition.RequestCreated,
            DiagnosticCollectorTransition.CollectorDispatchRequested,
            DiagnosticCollectorTransition.ProcessStartRequested,
            DiagnosticCollectorTransition.SupervisorProcessStartReturned,
            DiagnosticCollectorTransition.ContainmentPrepared,
            DiagnosticCollectorTransition.ContainmentEstablished,
            DiagnosticCollectorTransition.ControlPipeConnectionCompleted,
            DiagnosticCollectorTransition.StatusPipeConnectionCompleted,
            DiagnosticCollectorTransition.OwnershipAcknowledgementReceived,
            DiagnosticCollectorTransition.LaunchAuthorizationWritten,
            DiagnosticCollectorTransition.TargetStartAcknowledgementReceived,
            DiagnosticCollectorTransition.ProcessStarted
        ];
        private readonly DiagnosticCollectorRequest _request;
        private readonly TimeSpan _operationDeadlineElapsed;
        private readonly Dictionary<DiagnosticCollectorTransition, DiagnosticCollectorTransitionEvidence>
            _transitions = new();
        private int? _supervisorProcessId;
        private int? _targetProcessId;
        private DiagnosticCollectorFailureInterval? _failureInterval;

        public DiagnosticCollectorTimelineBuilder(DiagnosticCollectorRequest request)
        {
            _request = request;
            var observation = request.Window.Budget.Observe();
            _operationDeadlineElapsed = checked(
                observation.Elapsed + observation.RemainingOperation);
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
            ObserveOwnedProcessProgress(lease);
            ObserveOwnedProcessSettlement(lease, targetExitedAfter);
        }

        public void ObserveOwnedProcessProgress(OwnedProcessLease lease)
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

        }

        public void ObserveOwnedProcessSettlement(
            OwnedProcessLease lease,
            TimeSpan? targetExitedAfter)
        {
            ArgumentNullException.ThrowIfNull(lease);
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

            MarkIfObserved(
                DiagnosticCollectorTransition.FailureTerminationBegan,
                lease.FailureTerminationBeganAfter,
                "Failure cleanup termination began under the process owner.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureTerminationCompleted,
                lease.FailureTerminationCompletedAfter,
                "Failure cleanup termination settled under the process owner.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureTreeQuiescenceBegan,
                lease.FailureTreeQuiescenceBeganAfter,
                "Failure cleanup tree-quiescence observation began.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureTreeQuiescenceCompleted,
                lease.FailureTreeQuiescenceCompletedAfter,
                "Failure cleanup tree-quiescence observation settled.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureSupervisorReapBegan,
                lease.FailureSupervisorReapBeganAfter,
                "Failure cleanup supervisor reap began.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureSupervisorReapCompleted,
                lease.FailureSupervisorReapCompletedAfter,
                "Failure cleanup supervisor reap settled.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureStreamDrainBegan,
                lease.FailureStreamDrainBeganAfter,
                "Failure cleanup stream drain began.");
            MarkIfObserved(
                DiagnosticCollectorTransition.FailureStreamDrainCompleted,
                lease.FailureStreamDrainCompletedAfter,
                "Failure cleanup stream drain settled.");
        }

        public void MarkOperationDeadlineExhausted()
        {
            Mark(
                DiagnosticCollectorTransition.OperationDeadlineExhausted,
                _operationDeadlineElapsed,
                "The existing operation deadline was exhausted on the shared monotonic timeline.");
            var observedAt = _request.Window.Budget.Elapsed;
            Mark(
                DiagnosticCollectorTransition.OperationDeadlineExhaustionObserved,
                observedAt < _operationDeadlineElapsed
                    ? _operationDeadlineElapsed
                    : observedAt,
                "The collector owner observed operation-deadline exhaustion.");
        }

        public void RecordFailureInterval()
        {
            _failureInterval ??= BuildFailureInterval();
        }

        public void ObserveOwnedProcessStart(OwnedProcessStartTimeline startTimeline)
        {
            ArgumentNullException.ThrowIfNull(startTimeline);
            _supervisorProcessId = startTimeline.SupervisorProcessId;
            _targetProcessId = startTimeline.TargetProcessId;
            foreach (var transition in Enum.GetValues<OwnedProcessStartTransition>())
            {
                if (startTimeline.TryGetElapsed(transition, out var elapsed))
                {
                    Mark(
                        MapStartTransition(transition),
                        elapsed,
                        GetStartTransitionDetail(transition));
                }
            }
        }

        public void MarkTypedOutcomeReturned()
        {
            Mark(
                DiagnosticCollectorTransition.TypedOutcomeReturned,
                "The collector boundary returned or threw its typed result.");
        }

        public DiagnosticCollectorTimeline BuildPrimaryTimeline(
            DiagnosticCollectorMutation mutation)
        {
            if (mutation.HasFlag(DiagnosticCollectorMutation.SuppressPrimaryTimeline))
            {
                return new DiagnosticCollectorTimeline(
                    Array.Empty<DiagnosticCollectorTransitionEvidence>());
            }

            if (_transitions.ContainsKey(DiagnosticCollectorTransition.ProcessStarted))
            {
                MarkNotObservable(
                    DiagnosticCollectorTransition.TargetAttachBegan,
                    "The generic collector owner cannot observe an external tool's attach boundary.");
                MarkNotObservable(
                    DiagnosticCollectorTransition.StackCaptureBegan,
                    "The generic collector owner cannot observe an external tool's capture boundary.");
            }

            var entries = OrderedTransitions
                .Select(transition => _transitions.TryGetValue(transition, out var evidence)
                    ? evidence
                    : new DiagnosticCollectorTransitionEvidence(
                        transition,
                        DiagnosticCollectorTransitionState.NotObserved,
                        ElapsedMilliseconds: null,
                        Detail: "The transition was not observed before the typed result."))
                .OrderBy(entry =>
                    entry.State == DiagnosticCollectorTransitionState.Observed ? 0 : 1)
                .ThenBy(entry => entry.ElapsedMilliseconds ?? double.MaxValue)
                .ThenBy(entry => (int)entry.Transition)
                .ToArray();
            return new DiagnosticCollectorTimeline(
                new ReadOnlyCollection<DiagnosticCollectorTransitionEvidence>(entries));
        }

        public DiagnosticCollectorOwnerJournal BuildOwnerJournal(
            DiagnosticCollectorFailureKind? failureKind,
            IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
        {
            ArgumentNullException.ThrowIfNull(cleanupFailures);
            var entries = OrderedTransitions
                .Where(_transitions.ContainsKey)
                .Select(transition => _transitions[transition])
                .Where(entry =>
                    entry.State == DiagnosticCollectorTransitionState.Observed)
                .OrderBy(entry => entry.ElapsedMilliseconds)
                .ThenBy(entry => (int)entry.Transition)
                .ToArray();
            var cleanupKinds = cleanupFailures
                .Select(failure => failure.Kind)
                .ToArray();
            var reapFailed = cleanupKinds.Any(kind => kind is
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded or
                DiagnosticCollectorCleanupFailureKind.ReapFailed);
            var drainFailed = cleanupKinds.Contains(
                DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded);
            var terminateFailed = cleanupKinds.Contains(
                DiagnosticCollectorCleanupFailureKind.TerminateFailed);
            return new DiagnosticCollectorOwnerJournal(
                new ReadOnlyCollection<DiagnosticCollectorTransitionEvidence>(entries),
                failureKind.HasValue
                    ? _failureInterval ?? BuildFailureInterval()
                    : null,
                failureKind,
                new ReadOnlyCollection<DiagnosticCollectorCleanupFailureKind>(cleanupKinds),
                Contains(DiagnosticCollectorTransition.OperationDeadlineExhausted),
                Contains(DiagnosticCollectorTransition.ProcessStarted),
                Contains(DiagnosticCollectorTransition.ProcessExitObserved),
                Contains(DiagnosticCollectorTransition.StartFailureTerminationBegan) ||
                    Contains(DiagnosticCollectorTransition.FailureTerminationBegan),
                (Contains(
                        DiagnosticCollectorTransition.StartFailureTerminationCompleted) ||
                    Contains(DiagnosticCollectorTransition.FailureTerminationCompleted)) &&
                    !terminateFailed,
                (Contains(DiagnosticCollectorTransition.ReapCompleted) ||
                    Contains(
                        DiagnosticCollectorTransition.StartFailureSupervisorReapCompleted) ||
                    Contains(
                        DiagnosticCollectorTransition.FailureSupervisorReapCompleted)) &&
                    !reapFailed,
                (Contains(DiagnosticCollectorTransition.StreamsDrained) ||
                    Contains(
                        DiagnosticCollectorTransition.StartFailureStreamDrainCompleted) ||
                    Contains(
                        DiagnosticCollectorTransition.FailureStreamDrainCompleted)) &&
                    !drainFailed,
                _supervisorProcessId,
                _targetProcessId);
        }

        private DiagnosticCollectorFailureInterval BuildFailureInterval()
        {
            for (var index = 1; index < RequiredStartupTransitions.Length; index++)
            {
                var transition = RequiredStartupTransitions[index];
                if (!Contains(transition))
                {
                    return new DiagnosticCollectorFailureInterval(
                        RequiredStartupTransitions[index - 1],
                        transition,
                        ClassifyBoundary(transition));
                }
            }

            if (!Contains(DiagnosticCollectorTransition.FirstObservableProgress))
            {
                return new DiagnosticCollectorFailureInterval(
                    DiagnosticCollectorTransition.ProcessStarted,
                    DiagnosticCollectorTransition.FirstObservableProgress,
                    DiagnosticCollectorFailureBoundary.EvidenceCapture);
            }
            if (!Contains(DiagnosticCollectorTransition.StackOutputFirstByte))
            {
                return new DiagnosticCollectorFailureInterval(
                    DiagnosticCollectorTransition.FirstObservableProgress,
                    DiagnosticCollectorTransition.StackOutputFirstByte,
                    DiagnosticCollectorFailureBoundary.EvidenceCapture);
            }
            if (!Contains(DiagnosticCollectorTransition.ProcessExitObserved))
            {
                return new DiagnosticCollectorFailureInterval(
                    DiagnosticCollectorTransition.StackOutputFirstByte,
                    DiagnosticCollectorTransition.ProcessExitObserved,
                    DiagnosticCollectorFailureBoundary.TargetCompletion);
            }
            if (!Contains(DiagnosticCollectorTransition.ReapCompleted))
            {
                return new DiagnosticCollectorFailureInterval(
                    DiagnosticCollectorTransition.ProcessExitObserved,
                    DiagnosticCollectorTransition.ReapCompleted,
                    DiagnosticCollectorFailureBoundary.Cleanup);
            }
            if (!Contains(DiagnosticCollectorTransition.StreamsDrained))
            {
                return new DiagnosticCollectorFailureInterval(
                    DiagnosticCollectorTransition.ReapCompleted,
                    DiagnosticCollectorTransition.StreamsDrained,
                    DiagnosticCollectorFailureBoundary.Cleanup);
            }

            return new DiagnosticCollectorFailureInterval(
                DiagnosticCollectorTransition.StreamsDrained,
                DiagnosticCollectorTransition.TypedOutcomeReturned,
                DiagnosticCollectorFailureBoundary.OutcomeAggregation);
        }

        private bool Contains(DiagnosticCollectorTransition transition)
        {
            return _transitions.TryGetValue(transition, out var evidence) &&
                evidence.State == DiagnosticCollectorTransitionState.Observed;
        }

        private static DiagnosticCollectorFailureBoundary ClassifyBoundary(
            DiagnosticCollectorTransition firstMissingRequired)
        {
            return firstMissingRequired switch
            {
                DiagnosticCollectorTransition.CollectorDispatchRequested =>
                    DiagnosticCollectorFailureBoundary.CollectorDispatch,
                DiagnosticCollectorTransition.ProcessStartRequested or
                DiagnosticCollectorTransition.SupervisorProcessStartReturned =>
                    DiagnosticCollectorFailureBoundary.ProcessStart,
                DiagnosticCollectorTransition.ContainmentPrepared =>
                    DiagnosticCollectorFailureBoundary.ContainmentPreparation,
                DiagnosticCollectorTransition.ContainmentEstablished =>
                    DiagnosticCollectorFailureBoundary.ContainmentEstablishment,
                DiagnosticCollectorTransition.ControlPipeConnectionCompleted =>
                    DiagnosticCollectorFailureBoundary.ControlChannelStartup,
                DiagnosticCollectorTransition.StatusPipeConnectionCompleted =>
                    DiagnosticCollectorFailureBoundary.StatusChannelStartup,
                DiagnosticCollectorTransition.OwnershipAcknowledgementReceived =>
                    DiagnosticCollectorFailureBoundary.OwnershipHandshake,
                DiagnosticCollectorTransition.LaunchAuthorizationWritten or
                DiagnosticCollectorTransition.TargetStartAcknowledgementReceived or
                DiagnosticCollectorTransition.ProcessStarted =>
                    DiagnosticCollectorFailureBoundary.TargetLaunch,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(firstMissingRequired),
                    firstMissingRequired,
                    null)
            };
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

        private void MarkIfObserved(
            DiagnosticCollectorTransition transition,
            TimeSpan? elapsed,
            string detail)
        {
            if (elapsed.HasValue)
            {
                Mark(transition, elapsed.Value, detail);
            }
        }

        private static DiagnosticCollectorTransition MapStartTransition(
            OwnedProcessStartTransition transition)
        {
            return transition switch
            {
                OwnedProcessStartTransition.SupervisorProcessStartReturned =>
                    DiagnosticCollectorTransition.SupervisorProcessStartReturned,
                OwnedProcessStartTransition.ContainmentPrepared =>
                    DiagnosticCollectorTransition.ContainmentPrepared,
                OwnedProcessStartTransition.ContainmentEstablished =>
                    DiagnosticCollectorTransition.ContainmentEstablished,
                OwnedProcessStartTransition.ControlPipeConnectionCompleted =>
                    DiagnosticCollectorTransition.ControlPipeConnectionCompleted,
                OwnedProcessStartTransition.StatusPipeConnectionCompleted =>
                    DiagnosticCollectorTransition.StatusPipeConnectionCompleted,
                OwnedProcessStartTransition.OwnershipAcknowledgementReceived =>
                    DiagnosticCollectorTransition.OwnershipAcknowledgementReceived,
                OwnedProcessStartTransition.LaunchAuthorizationWritten =>
                    DiagnosticCollectorTransition.LaunchAuthorizationWritten,
                OwnedProcessStartTransition.TargetStartAcknowledgementReceived =>
                    DiagnosticCollectorTransition.TargetStartAcknowledgementReceived,
                OwnedProcessStartTransition.StartFailureObserved =>
                    DiagnosticCollectorTransition.StartFailureObserved,
                OwnedProcessStartTransition.OperationDeadlineExhausted =>
                    DiagnosticCollectorTransition.OperationDeadlineExhausted,
                OwnedProcessStartTransition.OperationDeadlineExhaustionObserved =>
                    DiagnosticCollectorTransition.OperationDeadlineExhaustionObserved,
                OwnedProcessStartTransition.StartFailureTerminationBegan =>
                    DiagnosticCollectorTransition.StartFailureTerminationBegan,
                OwnedProcessStartTransition.StartFailureTerminationCompleted =>
                    DiagnosticCollectorTransition.StartFailureTerminationCompleted,
                OwnedProcessStartTransition.StartFailureTreeQuiescenceBegan =>
                    DiagnosticCollectorTransition.StartFailureTreeQuiescenceBegan,
                OwnedProcessStartTransition.StartFailureTreeQuiescenceCompleted =>
                    DiagnosticCollectorTransition.StartFailureTreeQuiescenceCompleted,
                OwnedProcessStartTransition.StartFailureSupervisorReapBegan =>
                    DiagnosticCollectorTransition.StartFailureSupervisorReapBegan,
                OwnedProcessStartTransition.StartFailureSupervisorReapCompleted =>
                    DiagnosticCollectorTransition.StartFailureSupervisorReapCompleted,
                OwnedProcessStartTransition.StartFailureStreamDrainBegan =>
                    DiagnosticCollectorTransition.StartFailureStreamDrainBegan,
                OwnedProcessStartTransition.StartFailureStreamDrainCompleted =>
                    DiagnosticCollectorTransition.StartFailureStreamDrainCompleted,
                _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
            };
        }

        private static string GetStartTransitionDetail(OwnedProcessStartTransition transition)
        {
            return transition switch
            {
                OwnedProcessStartTransition.SupervisorProcessStartReturned =>
                    "The supervisor Process.Start call returned.",
                OwnedProcessStartTransition.ContainmentPrepared =>
                    "The platform containment owner was prepared.",
                OwnedProcessStartTransition.ContainmentEstablished =>
                    "The supervisor was placed under platform containment.",
                OwnedProcessStartTransition.ControlPipeConnectionCompleted =>
                    "The supervisor control channel connected.",
                OwnedProcessStartTransition.StatusPipeConnectionCompleted =>
                    "The supervisor status channel connected.",
                OwnedProcessStartTransition.OwnershipAcknowledgementReceived =>
                    "The supervisor acknowledged its containment ownership.",
                OwnedProcessStartTransition.LaunchAuthorizationWritten =>
                    "The immutable target launch authorization was written.",
                OwnedProcessStartTransition.TargetStartAcknowledgementReceived =>
                    "The supervisor acknowledged target start.",
                OwnedProcessStartTransition.StartFailureObserved =>
                    "The process owner observed the causal start failure.",
                OwnedProcessStartTransition.OperationDeadlineExhausted =>
                    "The existing operation deadline was exhausted on the shared monotonic timeline.",
                OwnedProcessStartTransition.OperationDeadlineExhaustionObserved =>
                    "The process owner observed that the operation deadline was exhausted.",
                OwnedProcessStartTransition.StartFailureTerminationBegan =>
                    "Start-failure termination began under the process owner.",
                OwnedProcessStartTransition.StartFailureTerminationCompleted =>
                    "Start-failure termination settled under the process owner.",
                OwnedProcessStartTransition.StartFailureTreeQuiescenceBegan =>
                    "Start-failure tree-quiescence observation began.",
                OwnedProcessStartTransition.StartFailureTreeQuiescenceCompleted =>
                    "Start-failure tree-quiescence observation settled.",
                OwnedProcessStartTransition.StartFailureSupervisorReapBegan =>
                    "Start-failure supervisor reap began.",
                OwnedProcessStartTransition.StartFailureSupervisorReapCompleted =>
                    "Start-failure supervisor reap settled.",
                OwnedProcessStartTransition.StartFailureStreamDrainBegan =>
                    "Start-failure supervisor stream drain began.",
                OwnedProcessStartTransition.StartFailureStreamDrainCompleted =>
                    "Start-failure supervisor stream drain settled.",
                _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
            };
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
