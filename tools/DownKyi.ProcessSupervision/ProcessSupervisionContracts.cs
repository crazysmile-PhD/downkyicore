using System.Collections.ObjectModel;

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
    TrustedChildProcessGroup
}

public sealed record ProcessOwnershipMetadata(
    ProcessIdentityAuthority IdentityAuthority,
    ProcessContainmentKind ContainmentKind,
    ProcessContainmentStrength ContainmentStrength,
    string ContainmentId,
    bool OwnershipEstablished,
    bool OwnerWasAlreadyContained);

public sealed record OwnedProcessOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TreeQuiescent,
    ProcessOwnershipMetadata Ownership);

public sealed record ParentLifetimeOutcome(bool ExactParentExited);

public abstract class ParentLifetimeLease : IAsyncDisposable
{
    public abstract ProcessIdentityAuthority IdentityAuthority { get; }

    public abstract ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        TransitionBudget budget,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

internal enum ProcessOwnershipMutation
{
    None,
    ResumeTargetBeforeOwnership
}
