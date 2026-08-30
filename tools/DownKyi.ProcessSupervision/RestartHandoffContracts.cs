using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

#pragma warning disable CA1515 // The executable supervisor intentionally exports restart-handoff contracts to production owners.

namespace DownKyi.ProcessSupervision;

public enum RestartHandoffState
{
    Prepared,
    WatcherReady,
    Authorized,
    Committed,
    Revoked,
    ParentExited,
    RelaunchStarted,
    Completed,
    Failed
}

public enum RestartHandoffFailureKind
{
    PrepareFailed,
    WatcherFailed,
    AuthorizationFailed,
    ParentExitedBeforeReady,
    CommitChannelClosed,
    AuthorizationRejected,
    DeadlineExceeded,
    ParentWaitFailed,
    RelaunchFailed,
    RevocationFailed,
    HelperCrashed
}

public enum RestartHandoffRequestParseResult
{
    NotRequested,
    Valid,
    Invalid
}

public enum RestartHandoffCleanupStage
{
    StatusEndpoint,
    AuthorizationEndpoint,
    ParentLifetime
}

public sealed record RestartHandoffFailure(
    RestartHandoffFailureKind Kind,
    RestartHandoffState State,
    ProcessIdentityAuthority? ParentIdentityAuthority,
    int? HelperProcessId,
    string Detail);

public sealed record RestartHandoffCleanupFailure(
    RestartHandoffCleanupStage Stage,
    string CauseType,
    string Detail)
{
    internal static RestartHandoffCleanupFailure FromException(
        RestartHandoffCleanupStage stage,
        Exception failure)
    {
        return new RestartHandoffCleanupFailure(
            stage,
            failure.GetType().FullName ?? failure.GetType().Name,
            failure.Message);
    }
}

public sealed record RestartHandoffOutcome(
    RestartHandoffState State,
    ProcessIdentityAuthority? ParentIdentityAuthority,
    int RelaunchAttempts,
    RestartHandoffFailure? Failure)
{
    public IReadOnlyList<RestartHandoffCleanupFailure> CleanupFailures { get; init; } =
        Array.Empty<RestartHandoffCleanupFailure>();

    public bool Succeeded =>
        State == RestartHandoffState.Completed &&
        Failure == null &&
        CleanupFailures.Count == 0;
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Restart handoff failures always carry typed transition and cleanup evidence.")]
public sealed class RestartHandoffException : Exception
{
    public RestartHandoffException(
        RestartHandoffFailure failure,
        Exception? cause = null,
        IReadOnlyList<Exception>? cleanupFailures = null)
        : base(CreateMessage(failure, cleanupFailures), cause)
    {
        Failure = failure;
        CleanupFailures = new ReadOnlyCollection<Exception>(
            cleanupFailures?.ToArray() ?? []);
    }

    public RestartHandoffFailure Failure { get; }

    public IReadOnlyList<Exception> CleanupFailures { get; }

    private static string CreateMessage(
        RestartHandoffFailure failure,
        IReadOnlyList<Exception>? cleanupFailures)
    {
        var suffix = cleanupFailures is { Count: > 0 }
            ? $" Cleanup reported {cleanupFailures.Count} failure(s)."
            : string.Empty;
        return $"Restart handoff failed ({failure.Kind}) in state {failure.State}: " +
            failure.Detail + suffix;
    }
}

public sealed record RestartHandoffDeadline
{
    internal RestartHandoffDeadline(
        string domain,
        long operationExpiresAt,
        long cleanupExpiresAt,
        long frequency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationExpiresAt);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupExpiresAt, operationExpiresAt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        Domain = domain;
        OperationExpiresAt = operationExpiresAt;
        CleanupExpiresAt = cleanupExpiresAt;
        Frequency = frequency;
    }

    public string Domain { get; }

    public long OperationExpiresAt { get; }

    public long CleanupExpiresAt { get; }

    public long Frequency { get; }

    public TimeSpan RemainingOperation => Remaining(OperationExpiresAt);

    public TimeSpan RemainingCleanup => Remaining(CleanupExpiresAt);

    internal static RestartHandoffDeadline Create(
        long startedAt,
        TimeSpan operationDuration,
        TimeSpan hardDuration,
        long frequency)
    {
        return new RestartHandoffDeadline(
            CurrentDomain(),
            checked(startedAt + DurationToTimestampTicks(operationDuration, frequency)),
            checked(startedAt + DurationToTimestampTicks(hardDuration, frequency)),
            frequency);
    }

    internal void ValidateCurrentClock()
    {
        if (!string.Equals(Domain, CurrentDomain(), StringComparison.Ordinal) ||
            Frequency != Stopwatch.Frequency)
        {
            throw new InvalidOperationException(
                "The restart handoff monotonic clock domain does not match the current process.");
        }
    }

    internal int RemainingOperationMillisecondsCeiling()
    {
        return RemainingMillisecondsCeiling(OperationExpiresAt);
    }

    private static string CurrentDomain()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows-qpc-v1";
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return "unix-monotonic-v1";
        }

        throw new PlatformNotSupportedException(
            "Restart handoff has no monotonic clock contract for this operating system.");
    }

    private static long DurationToTimestampTicks(TimeSpan duration, long frequency)
    {
        return checked((long)Math.Ceiling(duration.TotalSeconds * frequency));
    }

    private TimeSpan Remaining(long expiresAt)
    {
        var remaining = expiresAt - Stopwatch.GetTimestamp();
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remaining / (double)Frequency);
    }

    private int RemainingMillisecondsCeiling(long expiresAt)
    {
        var remaining = expiresAt - Stopwatch.GetTimestamp();
        return remaining <= 0
            ? 0
            : checked((int)Math.Min(
                int.MaxValue,
                Math.Ceiling(remaining * 1000d / Frequency)));
    }
}

public sealed record RestartHandoffRequest
{
    internal RestartHandoffRequest(
        int parentProcessId,
        string authorizationEndpoint,
        string statusEndpoint,
        RestartHandoffDeadline deadline,
        byte[] nonce)
    {
        ParentProcessId = parentProcessId;
        AuthorizationEndpoint = authorizationEndpoint;
        StatusEndpoint = statusEndpoint;
        Deadline = deadline;
        Nonce = nonce;
    }

    public int ParentProcessId { get; }

    internal string AuthorizationEndpoint { get; }

    internal string StatusEndpoint { get; }

    public RestartHandoffDeadline Deadline { get; }

    internal byte[] Nonce { get; }
}

public static class RestartHandoffProtocol
{
    internal const string MarkerArgument = "--downkyi-restart-handoff-v1";
    internal const string ParentProcessIdArgument = "--restart-parent-process-id";
    internal const string AuthorizationEndpointArgument = "--restart-authorization-endpoint";
    internal const string StatusEndpointArgument = "--restart-status-endpoint";
    internal const string DeadlineDomainArgument = "--restart-deadline-domain";
    internal const string OperationExpiryArgument = "--restart-operation-expires-at";
    internal const string CleanupExpiryArgument = "--restart-cleanup-expires-at";
    internal const string ClockFrequencyArgument = "--restart-clock-frequency";
    internal const string NonceArgument = "--restart-authorization-nonce";
    internal const int NonceLength = 32;
    private const int ProtocolArgumentCount = 17;

    public static RestartHandoffRequestParseResult ParseRequest(
        IReadOnlyList<string> arguments,
        out RestartHandoffRequest? request)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        var markerIndexes = arguments
            .Select((argument, index) => (argument, index))
            .Where(item => string.Equals(
                item.argument,
                MarkerArgument,
                StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (markerIndexes.Length == 0)
        {
            return RestartHandoffRequestParseResult.NotRequested;
        }

        if (markerIndexes.Length != 1)
        {
            return RestartHandoffRequestParseResult.Invalid;
        }

        var markerIndex = markerIndexes[0];
        if (arguments.Count - markerIndex != ProtocolArgumentCount ||
            !Matches(arguments, markerIndex + 1, ParentProcessIdArgument) ||
            !int.TryParse(
                arguments[markerIndex + 2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId) ||
            parentProcessId <= 0 ||
            !Matches(arguments, markerIndex + 3, AuthorizationEndpointArgument) ||
            !IsPhysicalEndpoint(arguments[markerIndex + 4]) ||
            !Matches(arguments, markerIndex + 5, StatusEndpointArgument) ||
            !IsPhysicalEndpoint(arguments[markerIndex + 6]) ||
            !Matches(arguments, markerIndex + 7, DeadlineDomainArgument) ||
            string.IsNullOrWhiteSpace(arguments[markerIndex + 8]) ||
            !Matches(arguments, markerIndex + 9, OperationExpiryArgument) ||
            !long.TryParse(
                arguments[markerIndex + 10],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var operationExpiresAt) ||
            operationExpiresAt <= 0 ||
            !Matches(arguments, markerIndex + 11, CleanupExpiryArgument) ||
            !long.TryParse(
                arguments[markerIndex + 12],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cleanupExpiresAt) ||
            cleanupExpiresAt < operationExpiresAt ||
            !Matches(arguments, markerIndex + 13, ClockFrequencyArgument) ||
            !long.TryParse(
                arguments[markerIndex + 14],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var frequency) ||
            frequency <= 0 ||
            !Matches(arguments, markerIndex + 15, NonceArgument))
        {
            return RestartHandoffRequestParseResult.Invalid;
        }

        byte[] nonce;
        try
        {
            nonce = Convert.FromHexString(arguments[markerIndex + 16]);
        }
        catch (FormatException)
        {
            return RestartHandoffRequestParseResult.Invalid;
        }

        if (nonce.Length != NonceLength)
        {
            return RestartHandoffRequestParseResult.Invalid;
        }

        request = new RestartHandoffRequest(
            parentProcessId,
            arguments[markerIndex + 4],
            arguments[markerIndex + 6],
            new RestartHandoffDeadline(
                arguments[markerIndex + 8],
                operationExpiresAt,
                cleanupExpiresAt,
                frequency),
            nonce);
        return RestartHandoffRequestParseResult.Valid;
    }

    private static bool Matches(
        IReadOnlyList<string> arguments,
        int index,
        string expected)
    {
        return string.Equals(arguments[index], expected, StringComparison.Ordinal);
    }

    private static bool IsPhysicalEndpoint(string value)
    {
        return value.Length == IpcEndpointName.GeneratedPhysicalIdentifierLength &&
            value.StartsWith(IpcEndpointName.PhysicalIdentifierPrefix, StringComparison.Ordinal);
    }
}

internal sealed class RestartHandoffStateMachine
{
    public RestartHandoffState State { get; private set; } = RestartHandoffState.Prepared;

    public void Transition(RestartHandoffState expected, RestartHandoffState next)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Restart handoff transition {State} -> {next} expected {expected}.");
        }

        State = next;
    }

    public void Fail()
    {
        State = RestartHandoffState.Failed;
    }
}
