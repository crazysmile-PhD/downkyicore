using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports collector contracts to PowerShell and platform tests.

public sealed class DiagnosticCollectorWindow
{
    private readonly TimeProvider _timeProvider;

    internal DiagnosticCollectorWindow(
        TransitionBudget parentBudget,
        TransitionBudget budget,
        TimeProvider timeProvider)
    {
        ParentBudget = parentBudget;
        Budget = budget;
        _timeProvider = timeProvider;
    }

    public TimeSpan RemainingOperation => Budget.RemainingOperation;

    public TimeSpan RemainingCleanup => Budget.RemainingCleanup;

    internal TransitionBudget Budget { get; }

    internal TransitionBudget ParentBudget { get; }

    public async Task DelayAsync(
        TimeSpan requestedDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedDelay, TimeSpan.Zero);
        if (requestedDelay == TimeSpan.Zero)
        {
            return;
        }

        var remaining = RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The diagnostic collector window operation deadline is exhausted.");
        }

        try
        {
            await Task.Delay(requestedDelay, _timeProvider, cancellationToken)
                .WaitAsync(remaining, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(
                "The diagnostic collector delay exceeded its caller-allocated window.",
                failure);
        }
    }
}

public sealed class DiagnosticCollectorRequest
{
    public DiagnosticCollectorRequest(
        LaunchSpec launch,
        DiagnosticCollectorWindow window)
    {
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        Window = window ?? throw new ArgumentNullException(nameof(window));
        CreatedAfterWindowStart = window.Budget.Elapsed;
    }

    public LaunchSpec Launch { get; }

    public DiagnosticCollectorWindow Window { get; }

    internal TimeSpan CreatedAfterWindowStart { get; }
}

public enum DiagnosticCollectorTransition
{
    RequestCreated = 0,
    ProcessStartRequested = 1,
    ProcessStarted = 2,
    TargetAttachBegan = 3,
    FirstObservableProgress = 4,
    StackCaptureBegan = 5,
    StackOutputFirstByte = 6,
    ProcessExitObserved = 7,
    ReapCompleted = 8,
    StreamsDrained = 9,
    TypedOutcomeReturned = 10,
    SupervisorProcessStartReturned = 11,
    ContainmentPrepared = 12,
    ContainmentEstablished = 13,
    ControlPipeConnectionCompleted = 14,
    OwnershipAcknowledgementReceived = 15,
    LaunchAuthorizationWritten = 16,
    TargetStartAcknowledgementReceived = 17,
    StartFailureObserved = 18,
    OperationDeadlineExhausted = 19,
    OperationDeadlineExhaustionObserved = 20,
    StartFailureTerminationBegan = 21,
    StartFailureTerminationCompleted = 22,
    StartFailureTreeQuiescenceBegan = 23,
    StartFailureTreeQuiescenceCompleted = 24,
    StartFailureSupervisorReapBegan = 25,
    StartFailureSupervisorReapCompleted = 26,
    StartFailureStreamDrainBegan = 27,
    StartFailureStreamDrainCompleted = 28,
    CollectorDispatchRequested = 29,
    StatusPipeConnectionCompleted = 30,
    FailureTerminationBegan = 31,
    FailureTerminationCompleted = 32,
    FailureTreeQuiescenceBegan = 33,
    FailureTreeQuiescenceCompleted = 34,
    FailureSupervisorReapBegan = 35,
    FailureSupervisorReapCompleted = 36,
    FailureStreamDrainBegan = 37,
    FailureStreamDrainCompleted = 38
}

public enum DiagnosticCollectorTransitionState
{
    Observed,
    NotObserved,
    NotObservable
}

public sealed record DiagnosticCollectorTransitionEvidence(
    DiagnosticCollectorTransition Transition,
    DiagnosticCollectorTransitionState State,
    double? ElapsedMilliseconds,
    string? Detail)
{
    public string TransitionName => Transition.ToString();

    public string StateName => State.ToString();
}

public sealed record DiagnosticCollectorTimeline(
    IReadOnlyList<DiagnosticCollectorTransitionEvidence> Transitions);

public enum DiagnosticCollectorFailureBoundary
{
    CollectorDispatch,
    ProcessStart,
    ContainmentPreparation,
    ContainmentEstablishment,
    ControlChannelStartup,
    StatusChannelStartup,
    OwnershipHandshake,
    TargetLaunch,
    EvidenceCapture,
    TargetCompletion,
    Cleanup,
    OutcomeAggregation
}

public sealed record DiagnosticCollectorFailureInterval(
    DiagnosticCollectorTransition LastKnownGood,
    DiagnosticCollectorTransition FirstMissingRequired,
    DiagnosticCollectorFailureBoundary Boundary)
{
    public string LastKnownGoodName => LastKnownGood.ToString();

    public string FirstMissingRequiredName => FirstMissingRequired.ToString();

    public string BoundaryName => Boundary.ToString();
}

public sealed record DiagnosticCollectorOwnerJournal(
    IReadOnlyList<DiagnosticCollectorTransitionEvidence> Transitions,
    DiagnosticCollectorFailureInterval? FailureInterval,
    DiagnosticCollectorFailureKind? FailureKind,
    IReadOnlyList<DiagnosticCollectorCleanupFailureKind> CleanupFailures,
    bool DeadlineExhausted,
    bool TargetStarted,
    bool TargetExited,
    bool TerminationStarted,
    bool TerminationCompleted,
    bool ReapCompleted,
    bool StreamsDrained,
    int? SupervisorProcessId,
    int? TargetProcessId)
{
    public string? FailureKindName => FailureKind?.ToString();
}

public sealed record DiagnosticCollectorEvidence(
    bool Started,
    bool Exited,
    bool Reaped,
    bool StreamsDrained,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DiagnosticCollectorTimeline Timeline);

public sealed record DiagnosticCollectorOutcome(DiagnosticCollectorEvidence Evidence)
{
    public DiagnosticCollectorOwnerJournal? OwnerJournal { get; init; }
}

public enum DiagnosticCollectorFailureKind
{
    StartFailed,
    OperationDeadlineExceeded,
    CallerCancelled,
    CollectorTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CleanupFailed,
    ExecutionFailed
}

public enum DiagnosticCollectorCleanupFailureKind
{
    TerminateFailed,
    CollectorTreeNotQuiescent,
    ReapDeadlineExceeded,
    ReapFailed,
    StreamDrainDeadlineExceeded,
    DisposeFailed
}

public sealed record DiagnosticCollectorCleanupFailure(
    DiagnosticCollectorCleanupFailureKind Kind,
    Exception Cause);

public sealed record DiagnosticCollectorFailure(
    DiagnosticCollectorFailureKind Kind,
    DiagnosticCollectorEvidence Evidence,
    Exception Cause)
{
    public DiagnosticCollectorOwnerJournal? OwnerJournal { get; init; }
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "The collector boundary always requires typed primary and cleanup evidence.")]
public sealed class DiagnosticCollectorExecutionException : Exception
{
    internal DiagnosticCollectorExecutionException(
        DiagnosticCollectorFailure failure,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
        : base(CreateMessage(failure, cleanupFailures), failure.Cause)
    {
        Failure = failure;
        CleanupFailures = new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(
            cleanupFailures.ToArray());
    }

    public DiagnosticCollectorFailure Failure { get; }

    public IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures { get; }

    private static string CreateMessage(
        DiagnosticCollectorFailure failure,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
    {
        return cleanupFailures.Count == 0
            ? $"Diagnostic collector execution failed: {failure.Kind}."
            : $"Diagnostic collector execution failed ({failure.Kind}) and cleanup " +
              $"reported {cleanupFailures.Count} failure(s).";
    }
}

[Flags]
internal enum DiagnosticCollectorMutation
{
    None = 0,
    IgnoreAllocatedWindow = 1,
    FailAfterTerminate = 2,
    StallReap = 4,
    StallStreamDrain = 8,
    StallBeforeSupervisorPipeConnection = 16,
    SuppressPrimaryTimeline = 32,
    LinkExecutionCancellationIntoStartup = 64
}
