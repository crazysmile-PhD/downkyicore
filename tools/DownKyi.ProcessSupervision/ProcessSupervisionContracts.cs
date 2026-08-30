using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA1515 // The executable supervisor intentionally exports lease contracts to future owners.

namespace DownKyi.ProcessSupervision;

public sealed class LaunchSpec
{
    public LaunchSpec(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool closeStandardInput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        FileName = fileName;
        Arguments = new ReadOnlyCollection<string>(arguments.ToArray());
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        Environment = new ReadOnlyDictionary<string, string?>(
            environment == null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(environment, StringComparer.Ordinal));
        CloseStandardInput = closeStandardInput;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }

    public bool CloseStandardInput { get; }
}

public sealed class TransitionBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly TransitionBudget? _parent;
    private readonly long _startedAt;
    private readonly TimeSpan _operationDuration;
    private readonly TimeSpan _hardDuration;

    private TransitionBudget(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider timeProvider,
        TransitionBudget? parent = null)
    {
        _operationDuration = operationDuration;
        _hardDuration = checked(operationDuration + cleanupGrace);
        _timeProvider = timeProvider;
        _parent = parent;
        _startedAt = timeProvider.GetTimestamp();
    }

    public static TransitionBudget Start(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupGrace, TimeSpan.Zero);
        return new TransitionBudget(
            operationDuration,
            cleanupGrace,
            timeProvider ?? TimeProvider.System);
    }

    public TimeSpan RemainingOperation => Remaining(
        _operationDuration,
        _parent?.RemainingOperation);

    public TimeSpan RemainingCleanup => Remaining(
        _hardDuration,
        _parent?.RemainingCleanup);

    internal TimeSpan Elapsed => _timeProvider.GetElapsedTime(
        _startedAt,
        _timeProvider.GetTimestamp());

    internal TransitionBudgetObservation Observe()
    {
        var timestamp = _timeProvider.GetTimestamp();
        return new TransitionBudgetObservation(
            _timeProvider.GetElapsedTime(_startedAt, timestamp),
            RemainingOperationAt(timestamp));
    }

    internal RestartHandoffDeadline CreateRestartHandoffDeadline()
    {
        if (!ReferenceEquals(_timeProvider, TimeProvider.System) || _parent != null)
        {
            throw new InvalidOperationException(
                "A cross-process restart handoff requires a root system-monotonic transition budget.");
        }

        return RestartHandoffDeadline.Create(
            _startedAt,
            _operationDuration,
            _hardDuration,
            _timeProvider.TimestampFrequency);
    }

    public DiagnosticCollectorWindow AllocateDiagnosticCollectorWindow(
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationAllowance,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupAllowance, TimeSpan.Zero);
        if (RemainingOperation <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The transition owner cannot allocate a diagnostic collector window " +
                "after its operation deadline.");
        }

        return new DiagnosticCollectorWindow(
            this,
            new TransitionBudget(
                operationAllowance,
                cleanupAllowance,
                _timeProvider,
                this),
            _timeProvider);
    }

    private TimeSpan Remaining(TimeSpan duration, TimeSpan? parentRemaining)
    {
        var remaining = duration - _timeProvider.GetElapsedTime(
            _startedAt,
            _timeProvider.GetTimestamp());
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return parentRemaining.HasValue && parentRemaining.Value < remaining
            ? parentRemaining.Value
            : remaining;
    }

    private TimeSpan RemainingOperationAt(long timestamp)
    {
        return RemainingAt(
            _operationDuration,
            _parent?.RemainingOperationAt(timestamp),
            timestamp);
    }

    private TimeSpan RemainingAt(
        TimeSpan duration,
        TimeSpan? parentRemaining,
        long timestamp)
    {
        var remaining = duration - _timeProvider.GetElapsedTime(_startedAt, timestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return parentRemaining.HasValue && parentRemaining.Value < remaining
            ? parentRemaining.Value
            : remaining;
    }
}

internal readonly record struct TransitionBudgetObservation(
    TimeSpan Elapsed,
    TimeSpan RemainingOperation);

internal enum OwnedProcessStartTransition
{
    SupervisorProcessStartReturned,
    ContainmentPrepared,
    ContainmentEstablished,
    ControlPipeConnectionCompleted,
    StatusPipeConnectionCompleted,
    OwnershipAcknowledgementReceived,
    LaunchAuthorizationWritten,
    TargetStartAcknowledgementReceived,
    StartFailureObserved,
    OperationDeadlineExhausted,
    OperationDeadlineExhaustionObserved,
    StartFailureTerminationBegan,
    StartFailureTerminationCompleted,
    StartFailureTreeQuiescenceBegan,
    StartFailureTreeQuiescenceCompleted,
    StartFailureSupervisorReapBegan,
    StartFailureSupervisorReapCompleted,
    StartFailureStreamDrainBegan,
    StartFailureStreamDrainCompleted
}

internal sealed class OwnedProcessStartTimeline
{
    private readonly TransitionBudget _budget;
    private readonly TimeSpan _operationDeadlineElapsed;
    private readonly Action<OwnedProcessStartTransition>? _transitionObserverForTesting;
    private readonly Dictionary<OwnedProcessStartTransition, TimeSpan> _transitions = new();
    private readonly object _sync = new();
    private int? _supervisorProcessId;
    private int? _targetProcessId;

    public OwnedProcessStartTimeline(
        TransitionBudget budget,
        Action<OwnedProcessStartTransition>? transitionObserverForTesting = null)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _transitionObserverForTesting = transitionObserverForTesting;
        var observation = budget.Observe();
        _operationDeadlineElapsed = checked(
            observation.Elapsed + observation.RemainingOperation);
    }

    public void Mark(OwnedProcessStartTransition transition)
    {
        bool added;
        lock (_sync)
        {
            added = _transitions.TryAdd(transition, _budget.Elapsed);
        }

        if (added)
        {
            _transitionObserverForTesting?.Invoke(transition);
        }
    }

    public void MarkOperationDeadlineExhausted()
    {
        lock (_sync)
        {
            _transitions.TryAdd(
                OwnedProcessStartTransition.OperationDeadlineExhausted,
                _operationDeadlineElapsed);
        }
    }

    public void MarkOperationDeadlineExhaustionObserved()
    {
        var observedAt = _budget.Elapsed;
        lock (_sync)
        {
            _transitions.TryAdd(
                OwnedProcessStartTransition.OperationDeadlineExhaustionObserved,
                observedAt < _operationDeadlineElapsed
                    ? _operationDeadlineElapsed
                    : observedAt);
        }
    }

    public void SetSupervisorProcessId(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        lock (_sync)
        {
            _supervisorProcessId ??= processId;
        }
    }

    public void SetTargetProcessId(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        lock (_sync)
        {
            _targetProcessId ??= processId;
        }
    }

    public int? SupervisorProcessId
    {
        get
        {
            lock (_sync)
            {
                return _supervisorProcessId;
            }
        }
    }

    public int? TargetProcessId
    {
        get
        {
            lock (_sync)
            {
                return _targetProcessId;
            }
        }
    }

    public bool TryGetElapsed(
        OwnedProcessStartTransition transition,
        out TimeSpan elapsed)
    {
        lock (_sync)
        {
            return _transitions.TryGetValue(transition, out elapsed);
        }
    }
}

public enum ProcessIdentityAuthority
{
    WindowsProcessHandle,
    DirectChildWait,
    LinuxPidFd,
    MacOSKqueueProcessNote
}

public enum ProcessContainmentKind
{
    WindowsJobObject,
    PosixProcessGroup
}

public enum ProcessContainmentStrength
{
    KernelJobTree,
    TrustedChildProcessGroup,
    DelegatedCgroupTree
}

public enum ProcessMembershipAuthority
{
    WindowsJobObject,
    LinuxCgroupV2,
    MacOSLibprocProcessGroup
}

public sealed record ProcessOwnershipMetadata(
    ProcessIdentityAuthority IdentityAuthority,
    ProcessContainmentKind ContainmentKind,
    ProcessContainmentStrength ContainmentStrength,
    string ContainmentId,
    ProcessMembershipAuthority MembershipAuthority,
    string MembershipId,
    string OwnerLifetimeId,
    string BackendArchitecture,
    bool OwnershipEstablished,
    bool OwnerWasAlreadyContained);

public sealed class EvidenceHoldRequest
{
    public EvidenceHoldRequest(
        string targetEnvironmentVariable,
        byte completionSignal,
        byte acknowledgmentSignal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetEnvironmentVariable);
        if (targetEnvironmentVariable.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The evidence-hold environment variable name cannot contain '='.",
                nameof(targetEnvironmentVariable));
        }
        if (completionSignal == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionSignal),
                "The evidence-hold completion signal cannot be zero.");
        }
        if (acknowledgmentSignal == 0 || acknowledgmentSignal == completionSignal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acknowledgmentSignal),
                "The evidence-hold acknowledgment signal must be non-zero and distinct.");
        }

        TargetEnvironmentVariable = targetEnvironmentVariable;
        CompletionSignal = completionSignal;
        AcknowledgmentSignal = acknowledgmentSignal;
    }

    public string TargetEnvironmentVariable { get; }

    public byte CompletionSignal { get; }

    public byte AcknowledgmentSignal { get; }
}

public enum EvidenceCaptureCompletion
{
    Pending,
    Captured,
    Failed
}

public sealed record EvidenceHoldOutcome(
    bool Requested,
    bool Granted,
    EvidenceCaptureCompletion CaptureCompletion,
    bool Released,
    bool CompletionSignalDelivered,
    bool TargetAcknowledged)
{
    internal static EvidenceHoldOutcome CreateNotRequested()
    {
        return new EvidenceHoldOutcome(
            Requested: false,
            Granted: false,
            EvidenceCaptureCompletion.Pending,
            Released: false,
            CompletionSignalDelivered: false,
            TargetAcknowledged: false);
    }
}

public sealed record OwnedProcessOutcome(
    int SupervisorProcessId,
    int? TargetProcessId,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long TargetExitedAtUnixMilliseconds,
    TimeSpan TargetExitedAfter,
    bool TreeQuiescent,
    ProcessOwnershipMetadata Ownership,
    EvidenceHoldOutcome EvidenceHold);

public enum OwnedProcessFailureKind
{
    OperationDeadlineExceeded,
    OwnedTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CallerCancelled,
    ExecutionFailed,
    CleanupFailed
}

public sealed record OwnedProcessFailure(
    OwnedProcessFailureKind Kind,
    int SupervisorProcessId,
    int? TargetProcessId,
    string StandardOutput,
    string StandardError,
    long? TargetExitedAtUnixMilliseconds,
    TimeSpan? TargetExitedAfter,
    bool TreeQuiescent,
    ProcessOwnershipMetadata Ownership,
    EvidenceHoldOutcome EvidenceHold);

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This typed boundary always requires the immutable process failure and cleanup evidence.")]
public sealed class OwnedProcessExecutionException : Exception
{
    internal OwnedProcessExecutionException(
        OwnedProcessFailure failure,
        Exception operationFailure,
        IReadOnlyList<Exception> cleanupFailures)
        : base(CreateMessage(failure, cleanupFailures), operationFailure)
    {
        Failure = failure;
        CleanupStageFailures = new ReadOnlyCollection<OwnedProcessCleanupStageFailure>(
            cleanupFailures.Select(OwnedProcessCleanupStageFailure.FromException).ToArray());
        CleanupFailures = new ReadOnlyCollection<Exception>(
            CleanupStageFailures.Select(item => item.Cause).ToArray());
    }

    public OwnedProcessFailure Failure { get; }

    public IReadOnlyList<Exception> CleanupFailures { get; }

    internal IReadOnlyList<OwnedProcessCleanupStageFailure> CleanupStageFailures { get; }

    private static string CreateMessage(
        OwnedProcessFailure failure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        return cleanupFailures.Count == 0
            ? $"Owned process execution failed: {failure.Kind}."
            : $"Owned process execution failed ({failure.Kind}) and cleanup reported " +
              $"{cleanupFailures.Count} failure(s).";
    }
}

internal enum OwnedProcessCleanupStage
{
    Terminate,
    TreeQuiescence,
    Reap,
    StreamDrain,
    TargetExitProtocol,
    Dispose,
    Unknown
}

internal sealed record OwnedProcessCleanupStageFailure(
    OwnedProcessCleanupStage Stage,
    Exception Cause)
{
    public static OwnedProcessCleanupStageFailure FromException(Exception failure)
    {
        return failure is OwnedProcessCleanupStageException staged
            ? new OwnedProcessCleanupStageFailure(staged.Stage, staged.Cause)
            : new OwnedProcessCleanupStageFailure(
                OwnedProcessCleanupStage.Unknown,
                failure);
    }
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal transport exception always requires a cleanup stage and original cause.")]
internal sealed class OwnedProcessCleanupStageException : Exception
{
    public OwnedProcessCleanupStageException(
        OwnedProcessCleanupStage stage,
        Exception cause)
        : base(cause.Message, cause)
    {
        Stage = stage;
        Cause = cause;
    }

    public OwnedProcessCleanupStage Stage { get; }

    public Exception Cause { get; }
}

public sealed record ParentLifetimeOutcome(bool ExactParentExited);

public abstract class ParentLifetimeLease : IAsyncDisposable
{
    public abstract ProcessIdentityAuthority IdentityAuthority { get; }

    internal abstract bool IsExited();

    public ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        return WaitForExitCoreAsync(
            deadline,
            waitStartedForTesting: null,
            cancellationToken);
    }

    internal ValueTask<ParentLifetimeOutcome> WaitForExitForTestingAsync(
        RestartHandoffDeadline deadline,
        Action waitStartedForTesting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waitStartedForTesting);
        return WaitForExitCoreAsync(
            deadline,
            waitStartedForTesting,
            cancellationToken);
    }

    protected abstract ValueTask<ParentLifetimeOutcome> WaitForExitCoreAsync(
        RestartHandoffDeadline deadline,
        Action? waitStartedForTesting,
        CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}

[Flags]
internal enum ProcessOwnershipMutation
{
    None = 0,
    ResumeTargetBeforeOwnership = 1,
    FailAfterContainmentTermination = 2,
    FailAfterRootReap = 4,
    FailOwnershipEstablishment = 8,
    FailMembershipQuery = 16,
    StallLaunchPayloadRead = 32,
    DelayAfterTargetExitReport = 64,
    ReleaseAnchorBeforeMembership = 128,
    FailFixturePublication = 256,
    FailAfterMembershipAttachment = 512,
    StallStreamDrain = 1024,
    StallRootReap = 2048,
    SkipTargetStreamForwarding = 4096,
    StallBeforeSupervisorPipeConnection = 8192,
    FailResourceRelease = 16384
}
