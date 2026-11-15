using System;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal readonly record struct DownloadBandwidthLimits(
    long OverallBytesPerSecond,
    long TransferBytesPerSecond)
{
    public static DownloadBandwidthLimits FromKilobytesPerSecond(
        int overallKilobytesPerSecond,
        int transferKilobytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(overallKilobytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(transferKilobytesPerSecond);
        return new DownloadBandwidthLimits(
            checked(overallKilobytesPerSecond * 1024L),
            checked(transferKilobytesPerSecond * 1024L));
    }
}

/// <summary>
/// Owns the global token bucket and creates one additional bucket per media transfer.
/// All range workers for a transfer must share the same lease.
/// </summary>
internal sealed class DownloadBandwidthLimiter
{
    private const double BucketDurationSeconds = 0.1;

    private readonly object _sync = new();
    private readonly Func<DownloadBandwidthLimits> _limitsProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TokenBucket _overallBucket = new();

    public DownloadBandwidthLimiter(
        Func<DownloadBandwidthLimits> limitsProvider,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _limitsProvider = limitsProvider
            ?? throw new ArgumentNullException(nameof(limitsProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delayAsync = delayAsync
            ?? ((delay, cancellationToken) => Task.Delay(
                delay,
                _timeProvider,
                cancellationToken));
    }

    public TransferLease CreateTransferLease() => new(this);

    private bool IsLimited
    {
        get
        {
            var limits = _limitsProvider();
            return limits.OverallBytesPerSecond > 0 || limits.TransferBytesPerSecond > 0;
        }
    }

    private async ValueTask<DownloadReadPermit> AcquireAsync(
        TokenBucket transferBucket,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limits = _limitsProvider();
            ArgumentOutOfRangeException.ThrowIfNegative(limits.OverallBytesPerSecond);
            ArgumentOutOfRangeException.ThrowIfNegative(limits.TransferBytesPerSecond);
            if (limits.OverallBytesPerSecond == 0
                && limits.TransferBytesPerSecond == 0)
            {
                return new DownloadReadPermit(
                    maximumBytes,
                    OverallLimited: false,
                    TransferLimited: false);
            }

            TimeSpan wait;
            lock (_sync)
            {
                var timestamp = _timeProvider.GetTimestamp();
                _overallBucket.Update(
                    limits.OverallBytesPerSecond,
                    timestamp,
                    _timeProvider);
                transferBucket.Update(
                    limits.TransferBytesPerSecond,
                    timestamp,
                    _timeProvider);

                var targetBytes = maximumBytes;
                if (_overallBucket.IsLimited)
                {
                    targetBytes = Math.Min(targetBytes, _overallBucket.MaximumGrantBytes);
                }

                if (transferBucket.IsLimited)
                {
                    targetBytes = Math.Min(targetBytes, transferBucket.MaximumGrantBytes);
                }

                if (_overallBucket.CanTake(targetBytes)
                    && transferBucket.CanTake(targetBytes))
                {
                    _overallBucket.Take(targetBytes);
                    transferBucket.Take(targetBytes);
                    return new DownloadReadPermit(
                        targetBytes,
                        _overallBucket.IsLimited,
                        transferBucket.IsLimited);
                }

                var seconds = Math.Max(
                    _overallBucket.SecondsUntilAvailable(targetBytes),
                    transferBucket.SecondsUntilAvailable(targetBytes));
                wait = TimeSpan.FromSeconds(seconds);
                if (wait <= TimeSpan.Zero)
                {
                    wait = TimeSpan.FromTicks(1);
                }
            }

            await _delayAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReturnUnused(
        TokenBucket transferBucket,
        DownloadReadPermit permit,
        int unusedBytes)
    {
        if (unusedBytes <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(unusedBytes, permit.ByteCount);
        lock (_sync)
        {
            if (permit.OverallLimited)
            {
                _overallBucket.Return(unusedBytes);
            }

            if (permit.TransferLimited)
            {
                transferBucket.Return(unusedBytes);
            }
        }
    }

    internal sealed class TransferLease
    {
        private readonly DownloadBandwidthLimiter _owner;
        private readonly TokenBucket _transferBucket = new();

        public bool IsLimited => _owner.IsLimited;

        internal TransferLease(DownloadBandwidthLimiter owner)
        {
            _owner = owner;
        }

        public ValueTask<DownloadReadPermit> AcquireAsync(
            int maximumBytes,
            CancellationToken cancellationToken) =>
            _owner.AcquireAsync(_transferBucket, maximumBytes, cancellationToken);

        public void ReturnUnused(DownloadReadPermit permit, int unusedBytes) =>
            _owner.ReturnUnused(_transferBucket, permit, unusedBytes);
    }

    private sealed class TokenBucket
    {
        private long _bytesPerSecond = -1;
        private double _availableBytes;
        private double _capacityBytes;
        private long _updatedTimestamp;

        public bool IsLimited => _bytesPerSecond > 0;

        public int MaximumGrantBytes => IsLimited
            ? Math.Max(1, checked((int)Math.Min(Math.Floor(_capacityBytes), int.MaxValue)))
            : int.MaxValue;

        public void Update(
            long bytesPerSecond,
            long timestamp,
            TimeProvider timeProvider)
        {
            if (_bytesPerSecond == bytesPerSecond)
            {
                Refill(timestamp, timeProvider);
                return;
            }

            if (_bytesPerSecond > 0)
            {
                Refill(timestamp, timeProvider);
            }

            var wasLimited = IsLimited;
            _bytesPerSecond = bytesPerSecond;
            _capacityBytes = bytesPerSecond > 0
                ? Math.Max(1, bytesPerSecond * BucketDurationSeconds)
                : 0;
            _availableBytes = !wasLimited && bytesPerSecond > 0
                ? _capacityBytes
                : Math.Min(_availableBytes, _capacityBytes);
            _updatedTimestamp = timestamp;
        }

        public bool CanTake(int byteCount) => !IsLimited || _availableBytes >= byteCount;

        public void Take(int byteCount)
        {
            if (IsLimited)
            {
                _availableBytes -= byteCount;
            }
        }

        public void Return(int byteCount)
        {
            if (IsLimited)
            {
                _availableBytes = Math.Min(_capacityBytes, _availableBytes + byteCount);
            }
        }

        public double SecondsUntilAvailable(int byteCount)
        {
            return !IsLimited || _availableBytes >= byteCount
                ? 0
                : (byteCount - _availableBytes) / _bytesPerSecond;
        }

        private void Refill(long timestamp, TimeProvider timeProvider)
        {
            if (!IsLimited)
            {
                _updatedTimestamp = timestamp;
                return;
            }

            var elapsedSeconds = Math.Max(
                0,
                timeProvider.GetElapsedTime(_updatedTimestamp, timestamp).TotalSeconds);
            _availableBytes = Math.Min(
                _capacityBytes,
                _availableBytes + elapsedSeconds * _bytesPerSecond);
            _updatedTimestamp = timestamp;
        }
    }
}

internal readonly record struct DownloadReadPermit(
    int ByteCount,
    bool OverallLimited,
    bool TransferLimited);
