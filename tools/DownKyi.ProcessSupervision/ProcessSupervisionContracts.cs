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

    public TimeSpan RemainingOperation => Remaining(_operationDuration);

    public TimeSpan RemainingCleanup => Remaining(_hardDuration);

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
    WindowsProcessHandle,
    DirectChildWait
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

public sealed record OwnedProcessOutcome(
    int SupervisorProcessId,
    int? TargetProcessId,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long TargetExitedAtUnixMilliseconds,
    bool TreeQuiescent,
    ProcessOwnershipMetadata Ownership);

public enum OwnedProcessFailureKind
{
    OperationDeadlineExceeded,
    OwnedTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CallerCancelled,
    ExecutionFailed
}

public sealed record OwnedProcessFailure(
    OwnedProcessFailureKind Kind,
    int SupervisorProcessId,
    int? TargetProcessId,
    string StandardOutput,
    string StandardError,
    long? TargetExitedAtUnixMilliseconds,
    bool TreeQuiescent,
    ProcessOwnershipMetadata Ownership);

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
        CleanupFailures = new ReadOnlyCollection<Exception>(cleanupFailures.ToArray());
    }

    public OwnedProcessFailure Failure { get; }

    public IReadOnlyList<Exception> CleanupFailures { get; }

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

public sealed record ParentLifetimeOutcome(bool ExactParentExited);

public abstract class ParentLifetimeLease : IAsyncDisposable
{
    public abstract ProcessIdentityAuthority IdentityAuthority { get; }

    public abstract ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        TransitionBudget budget,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

[Flags]
internal enum ProcessOwnershipMutation
{
    None = 0,
    ResumeTargetBeforeOwnership = 1,
    FailAfterContainmentTermination = 2,
    FailAfterRootReap = 4,
    ReportTreeQuiescentOnce = 8,
    FailOwnershipEstablishment = 16,
    FailMembershipQuery = 32,
    StallLaunchPayloadRead = 64,
    DelayAfterTargetExitReport = 128,
    ReleaseAnchorBeforeMembership = 256,
    FailFixturePublication = 512
}
