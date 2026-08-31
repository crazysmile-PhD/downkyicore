using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA1515 // The shared process-supervision boundary is consumed by PowerShell and platform test projects.

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
    private readonly long _startedAt;
    private readonly TimeSpan _operationDuration;
    private readonly TimeSpan _hardDuration;

    private TransitionBudget(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider timeProvider)
    {
        _operationDuration = operationDuration;
        _hardDuration = checked(operationDuration + cleanupGrace);
        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetTimestamp();
    }

    public static TransitionBudget Start(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace)
    {
        return StartForTesting(
            operationDuration,
            cleanupGrace,
            TimeProvider.System);
    }

    internal TimeSpan RemainingOperation => Remaining(_operationDuration);

    internal TimeSpan RemainingCleanup => Remaining(_hardDuration);

    internal bool OperationExpired => RemainingOperation == TimeSpan.Zero;

    internal async Task<T> AwaitOperationAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await operation.WaitAsync(
                RemainingOperation,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task AwaitOperationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation.WaitAsync(
                RemainingOperation,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task AwaitCleanupAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation.WaitAsync(
                RemainingCleanup,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task DelayCleanupObservationAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var remaining = RemainingCleanup;
        if (remaining == TimeSpan.Zero)
        {
            throw new TimeoutException("The owned-process cleanup budget is exhausted.");
        }

        await Task.Delay(
                interval < remaining ? interval : remaining,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task DelayOperationObservationAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var remaining = RemainingOperation;
        if (remaining == TimeSpan.Zero)
        {
            throw new TimeoutException("The owned-process operation budget is exhausted.");
        }

        await Task.Delay(
                interval < remaining ? interval : remaining,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static TransitionBudget StartForTesting(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupGrace, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        return new TransitionBudget(
            operationDuration,
            cleanupGrace,
            timeProvider);
    }

    private TimeSpan Remaining(TimeSpan duration)
    {
        var remaining = duration - _timeProvider.GetElapsedTime(
            _startedAt,
            _timeProvider.GetTimestamp());
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

public enum ProcessIdentityAuthority
{
    Unspecified,
    WindowsProcessHandle,
    DirectChildWait
}

public enum ProcessContainmentKind
{
    Unspecified,
    WindowsJobObject,
    LinuxCgroupV2,
    LinuxProcessGroup,
    MacOSProcessGroup
}

public enum ProcessContainmentStrength
{
    Unspecified,
    KernelJobTree,
    DelegatedCgroupTree,
    TrustedChildProcessGroup
}

public enum ProcessContainmentRequirement
{
    AllowWeakerFallback,
    RequireStrongContainment
}

public enum ProcessMembershipAuthority
{
    Unspecified,
    WindowsJobAccounting,
    LinuxCgroupV2,
    LinuxProcessGroupSignal,
    MacOSLibprocProcessGroup
}

public sealed record ProcessOwnershipMetadata(
    ProcessIdentityAuthority IdentityAuthority,
    ProcessContainmentKind ContainmentKind,
    ProcessContainmentStrength ContainmentStrength,
    ProcessMembershipAuthority MembershipAuthority,
    string ContainmentId,
    string MembershipId,
    string OwnerLifetimeId,
    bool OwnershipEstablished);

public enum OwnedProcessInvariantKind
{
    TargetTerminal,
    RequiredContainment,
    OperationCompletion,
    OperationBudget,
    TreeQuiescence,
    BoundedCleanup,
    StreamDrain,
    OwnershipLifetime
}

public enum OwnedProcessInvariantState
{
    Unknown,
    Proven,
    Violated
}

public sealed record OwnedProcessInvariantResult(
    OwnedProcessInvariantKind Kind,
    OwnedProcessInvariantState State);

public enum OwnedProcessFactKind
{
    TargetStarted,
    TargetTerminal,
    ContainmentEstablished,
    ContainmentLost,
    CancellationRequested,
    OperationDeadlineExceeded,
    LifetimeCloseRequested,
    TerminationCompleted,
    ReapCompleted,
    TreeQuiescent,
    CleanupCompleted,
    StreamsDrained,
    OwnershipClosed
}

public sealed record OwnedProcessFact(
    OwnedProcessFactKind Kind,
    OwnedProcessFailurePhase Phase,
    string? Detail = null);

public enum OwnedProcessFailureKind
{
    Unspecified,
    ContainmentUnavailable,
    ContainmentSetupFailed,
    ContainmentLost,
    OperationDeadlineExceeded,
    OwnedTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CallerCancelled,
    LifetimeClosed,
    TargetExecutionFailed,
    SupervisorProtocolFailed,
    TerminationFailed,
    ReapFailed,
    CleanupDeadlineExceeded,
    ResourceReleaseFailed,
    RequiredInvariantUnknown,
    ExecutionFailed
}

public enum OwnedProcessFailurePhase
{
    Unspecified,
    Start,
    OwnershipEstablishment,
    TargetExecution,
    Termination,
    Reap,
    Quiescence,
    SupervisorFinalization,
    StreamDrain,
    ResourceRelease
}

public enum OwnedProcessFailureChannel
{
    Operation,
    Cleanup
}

public sealed record OwnedProcessFailure(
    OwnedProcessFailureKind Kind,
    OwnedProcessFailurePhase Phase,
    OwnedProcessFailureChannel Channel,
    string ErrorType,
    string Message);

public sealed class OwnedProcessOutcome
{
    internal OwnedProcessOutcome(
        int supervisorProcessId,
        int? targetProcessId,
        int? exitCode,
        string standardOutput,
        string standardError,
        long? targetExitObservedAtUnixMilliseconds,
        ProcessOwnershipMetadata ownership,
        IEnumerable<OwnedProcessInvariantResult> invariants,
        IEnumerable<OwnedProcessFact> facts,
        IEnumerable<OwnedProcessFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(invariants);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(failures);

        SupervisorProcessId = supervisorProcessId;
        TargetProcessId = targetProcessId;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        TargetExitObservedAtUnixMilliseconds = targetExitObservedAtUnixMilliseconds;
        Ownership = ownership;
        Invariants = new ReadOnlyCollection<OwnedProcessInvariantResult>(
            invariants.ToArray());
        Facts = new ReadOnlyCollection<OwnedProcessFact>(facts.ToArray());
        Failures = new ReadOnlyCollection<OwnedProcessFailure>(failures.ToArray());
    }

    public int SupervisorProcessId { get; }

    public int? TargetProcessId { get; }

    public int? ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public long? TargetExitObservedAtUnixMilliseconds { get; }

    public ProcessOwnershipMetadata Ownership { get; }

    public IReadOnlyList<OwnedProcessInvariantResult> Invariants { get; }

    public IReadOnlyList<OwnedProcessFact> Facts { get; }

    public IReadOnlyList<OwnedProcessFailure> Failures { get; }

    public bool FormalGatePassed
    {
        get
        {
            var required = Enum.GetValues<OwnedProcessInvariantKind>();
            return Invariants.Count == required.Length &&
                   required.All(kind =>
                       Invariants.Count(invariant =>
                           invariant.Kind == kind &&
                           invariant.State == OwnedProcessInvariantState.Proven) == 1);
        }
    }
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This typed boundary always requires immutable process and cleanup evidence.")]
public sealed class OwnedProcessExecutionException : Exception
{
    internal OwnedProcessExecutionException(OwnedProcessOutcome outcome)
        : base(CreateMessage(outcome))
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }

    public OwnedProcessOutcome Outcome { get; }

    private static string CreateMessage(OwnedProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var violated = outcome.Invariants.Count(
            invariant => invariant.State == OwnedProcessInvariantState.Violated);
        var unknown = outcome.Invariants.Count(
            invariant => invariant.State == OwnedProcessInvariantState.Unknown);
        var blockingStates = string.Join(
            ", ",
            outcome.Invariants
                .Where(invariant => invariant.State != OwnedProcessInvariantState.Proven)
                .Select(invariant => $"{invariant.Kind}={invariant.State}"));
        var typedFailures = string.Join(
            ", ",
            outcome.Failures.Select(failure =>
                $"{failure.Kind}@{failure.Phase}/{failure.Channel}:{failure.ErrorType}"));
        return $"Owned process proof gate failed: {violated} violated, {unknown} unknown, " +
               $"{outcome.Failures.Count} typed failure(s). " +
               $"Invariants=[{blockingStates}]; Failures=[{typedFailures}].";
    }
}
