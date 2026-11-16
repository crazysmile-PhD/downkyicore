using System;
using System.Threading;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadProgressUiUpdater
{
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumInterval;
    private readonly Lock _sync = new();
    private DateTimeOffset? _lastPublishedAtUtc;

    public DownloadProgressUiUpdater(TimeProvider timeProvider, TimeSpan minimumInterval)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumInterval, TimeSpan.Zero);
        _minimumInterval = minimumInterval;
    }

    public bool TryCreate(
        double progressPercentage,
        double receivedBytes,
        double totalBytes,
        long bytesPerSecond,
        out DownloadProgress progress)
    {
        bytesPerSecond = Math.Max(0, bytesPerSecond);
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        lock (_sync)
        {
            if (progressPercentage < 100
                && _lastPublishedAtUtc is { } lastPublishedAtUtc
                && nowUtc >= lastPublishedAtUtc
                && nowUtc - lastPublishedAtUtc < _minimumInterval)
            {
                progress = DownloadProgress.None;
                return false;
            }

            _lastPublishedAtUtc = nowUtc;
        }

        var downloadedBytes = NormalizeByteCount(receivedBytes);
        var totalByteCount = NormalizeByteCount(totalBytes);
        progress = new DownloadProgress(
            Math.Clamp(progressPercentage, 0, 100),
            downloadedBytes,
            Math.Max(downloadedBytes, totalByteCount),
            bytesPerSecond,
            $"{Format.FormatFileSize(downloadedBytes)}/{Format.FormatFileSize(totalByteCount)}",
            Format.FormatSpeedWithBandwidth(bytesPerSecond));
        return true;
    }

    private static long NormalizeByteCount(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue ? long.MaxValue : (long)value;
    }
}
