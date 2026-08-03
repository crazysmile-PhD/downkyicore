using System;

namespace DownKyi.Services.Download;

internal enum DownloadRetryAction
{
    Stop,
    RetrySameAddress,
    TryNextAddress,
    RefreshAddresses
}

internal sealed record DownloadRetryDecision(
    DownloadRetryAction Action,
    TimeSpan Delay);

internal sealed class DownloadRetryPolicy
{
    public const int DefaultMaximumAttempts = 5;
    private static readonly TimeSpan MaximumServerDelay = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _baseDelay;

    public DownloadRetryPolicy(
        int maximumAttempts = DefaultMaximumAttempts,
        TimeSpan? baseDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        MaximumAttempts = maximumAttempts;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public int MaximumAttempts { get; }

    public DownloadRetryDecision Decide(
        DownloadTransferResult result,
        int attempt,
        int attemptsForAddress,
        bool hasNextAddress,
        bool canRefreshAddresses)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsForAddress, 1);
        if (result.Outcome != DownloadTransferOutcome.Failed ||
            attempt >= MaximumAttempts)
        {
            return Stop();
        }

        return result.FailureKind switch
        {
            DownloadTransferFailureKind.TransientNetwork =>
                DecideTransient(attempt, attemptsForAddress, hasNextAddress),
            DownloadTransferFailureKind.RateLimited => new DownloadRetryDecision(
                DownloadRetryAction.RetrySameAddress,
                ClampServerDelay(result.RetryAfter ?? GetBackoff(attempt))),
            DownloadTransferFailureKind.ExpiredAddress =>
                MoveOrRefresh(hasNextAddress, canRefreshAddresses),
            DownloadTransferFailureKind.ResumeRejected =>
                RetrySameThenMove(attemptsForAddress, hasNextAddress),
            DownloadTransferFailureKind.InvalidMedia => hasNextAddress
                ? new DownloadRetryDecision(DownloadRetryAction.TryNextAddress, TimeSpan.Zero)
                : Stop(),
            DownloadTransferFailureKind.None or
                DownloadTransferFailureKind.Disk or
                DownloadTransferFailureKind.Tls or
                DownloadTransferFailureKind.Permanent => Stop(),
            _ => Stop()
        };
    }

    internal TimeSpan GetBackoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 6));
        return TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * multiplier);
    }

    private DownloadRetryDecision DecideTransient(
        int attempt,
        int attemptsForAddress,
        bool hasNextAddress)
    {
        if (attemptsForAddress < 2)
        {
            return new DownloadRetryDecision(
                DownloadRetryAction.RetrySameAddress,
                GetBackoff(attempt));
        }

        return hasNextAddress
            ? new DownloadRetryDecision(DownloadRetryAction.TryNextAddress, TimeSpan.Zero)
            : Stop();
    }

    private static DownloadRetryDecision RetrySameThenMove(
        int attemptsForAddress,
        bool hasNextAddress)
    {
        if (attemptsForAddress < 2)
        {
            return new DownloadRetryDecision(
                DownloadRetryAction.RetrySameAddress,
                TimeSpan.Zero);
        }

        return hasNextAddress
            ? new DownloadRetryDecision(
                DownloadRetryAction.TryNextAddress,
                TimeSpan.Zero)
            : Stop();
    }

    private static DownloadRetryDecision MoveOrRefresh(
        bool hasNextAddress,
        bool canRefreshAddresses)
    {
        if (hasNextAddress)
        {
            return new DownloadRetryDecision(
                DownloadRetryAction.TryNextAddress,
                TimeSpan.Zero);
        }

        return canRefreshAddresses
            ? new DownloadRetryDecision(
                DownloadRetryAction.RefreshAddresses,
                TimeSpan.Zero)
            : Stop();
    }

    private static DownloadRetryDecision Stop() =>
        new(DownloadRetryAction.Stop, TimeSpan.Zero);

    private static TimeSpan ClampServerDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaximumServerDelay ? MaximumServerDelay : delay;
    }
}
