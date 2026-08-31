#pragma warning disable CA1515 // This contract is intentionally public for the future process owner.

namespace DownKyi.ProcessSupervision;

public enum TransitionDeadlineKind
{
    Operation,
    Cleanup
}

public sealed class TransitionBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;

    private TransitionBudget(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupGrace, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetTimestamp();
        Operation = new TransitionDeadline(
            this,
            TransitionDeadlineKind.Operation,
            operationDuration);
        Cleanup = new TransitionDeadline(
            this,
            TransitionDeadlineKind.Cleanup,
            checked(operationDuration + cleanupGrace));
    }

    public TransitionDeadline Operation { get; }

    public TransitionDeadline Cleanup { get; }

    public static TransitionBudget Start(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace)
    {
        return new TransitionBudget(
            operationDuration,
            cleanupGrace,
            TimeProvider.System);
    }

    internal static TransitionBudget StartForTesting(
        TimeSpan operationDuration,
        TimeSpan cleanupGrace,
        TimeProvider timeProvider)
    {
        return new TransitionBudget(
            operationDuration,
            cleanupGrace,
            timeProvider);
    }

    internal TimeSpan Remaining(TimeSpan limit)
    {
        var elapsed = _timeProvider.GetElapsedTime(
            _startedAt,
            _timeProvider.GetTimestamp());
        var remaining = limit - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

public sealed class TransitionDeadline
{
    internal TransitionDeadline(
        TransitionBudget authority,
        TransitionDeadlineKind kind,
        TimeSpan limit)
    {
        Authority = authority;
        Kind = kind;
        Limit = limit;
    }

    public TransitionBudget Authority { get; }

    public TransitionDeadlineKind Kind { get; }

    public TimeSpan Limit { get; }

    public TimeSpan Remaining => Authority.Remaining(Limit);

    public bool IsExpired => Remaining == TimeSpan.Zero;
}
