using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

/// <summary>
/// One revocable attempt lease. A successor may start only after RunAsync has
/// drained its predecessor, including all range workers and checkpoints.
/// </summary>
internal sealed class DownloadTransferAttempt
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly DownloadTransferWatchdog _policy;
    private readonly Func<bool> _isLimited;
    private readonly ILogger _logger;
    private bool _revoked;
    private int _networkReaders;
    private long _bytes;
    private DownloadProgress? _progress;
    private long _firstProgressAt;
    private long _baselineBytes;

    public DownloadTransferAttempt(
        TimeProvider clock,
        DownloadTransferWatchdog policy,
        Func<bool> isLimited,
        ILogger logger)
    {
        _clock = clock;
        _policy = policy;
        _isLimited = isLimited;
        _logger = logger;
    }

    public async Task<DownloadTransferResult> RunAsync(
        ITransferBackend backend,
        DownloadTransferRequest request,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leasedRequest = request with
        {
            CancellationToken = lifetime.Token,
            PublishProgress = progress =>
            {
                lock (_gate)
                {
                    if (_revoked || lifetime.IsCancellationRequested)
                    {
                        return;
                    }

                    _bytes = progress.DownloadedBytes ?? 0;
                    if (_progress == null)
                    {
                        _firstProgressAt = _clock.GetTimestamp();
                        _baselineBytes = _bytes;
                    }
                    _progress = progress;
                    request.PublishProgress(progress);
                }
            },
            // Only the lease owner persists progress, after the worker stops.
            PersistProgressAsync = static (_, _) => Task.CompletedTask,
            NetworkReadStateChanged = waiting =>
            {
                lock (_gate)
                {
                    if (!_revoked)
                    {
                        _networkReaders += waiting ? 1 : -1;
                    }
                }
            }
        };
        DownloadTransferResult? interrupted = null;
        // Backend invocation is inside the lifetime: synchronous throws are safe too.
        Task<DownloadTransferResult>? worker = null;
        var observed = false;
        try
        {
            worker = backend.TransferAsync(leasedRequest);
            interrupted = await MonitorAsync(worker, request, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _revoked = true;
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            DownloadTransferResult? result = null;
            try
            {
                result = await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested &&
                (interrupted != null || cancellationToken.IsCancellationRequested))
            {
                // This cancellation acknowledges our revocation; the reason is below.
                _logger.LogDebugMessage("Transfer attempt revocation acknowledged by worker.");
            }
            finally
            {
                observed = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (request.IsPauseRequested())
            {
                return DownloadTransferResult.Paused();
            }

            request.EnsureActive();
            if (_progress != null)
            {
                await request.PersistProgressAsync(_progress, cancellationToken).ConfigureAwait(false);
            }

            return interrupted ?? result ?? throw new InvalidOperationException(
                "The transfer attempt returned no result.");
        }
        finally
        {
            lock (_gate)
            {
                _revoked = true;
            }

            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                if (worker != null && !observed)
                {
                    await DrainAsync(worker, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task DrainAsync(Task worker, CancellationToken revoked)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested)
        {
            // The guard/caller exception already owns the outcome. Join the
            // cancellation acknowledgement before propagating that exception.
            _logger.LogDebugMessage("Transfer attempt drained after its owner exited.");
        }
    }

    private async Task<DownloadTransferResult?> MonitorAsync(
        Task worker, DownloadTransferRequest request, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        var windowStarted = started;
        var windowBytes = 0L;
        var hasBaseline = false;
        var slowWindows = 0;
        using var timer = new PeriodicTimer(_policy.PollInterval, _clock);
        while (!worker.IsCompleted)
        {
            if (await Task.WhenAny(worker, timer.WaitForNextTickAsync(cancellationToken).AsTask())
                    .ConfigureAwait(false) == worker)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (request.IsPauseRequested())
            {
                return DownloadTransferResult.Paused();
            }

            request.EnsureActive();
            var now = _clock.GetTimestamp();
            lock (_gate)
            {
                if (_progress != null && !hasBaseline)
                {
                    hasBaseline = true;
                    windowBytes = _baselineBytes;
                    windowStarted = _firstProgressAt;
                }

                var elapsed = _clock.GetElapsedTime(windowStarted, now);
                if (_clock.GetElapsedTime(started, now) < _policy.StartupGrace || elapsed < _policy.Window)
                {
                    continue;
                }

                var rate = Math.Max(0, _bytes - windowBytes) / elapsed.TotalSeconds;
                slowWindows = _networkReaders > 0 && !_isLimited() && rate < _policy.MinimumBytesPerSecond
                    ? slowWindows + 1 : 0;
                windowBytes = _bytes;
                windowStarted = now;
                if (slowWindows >= _policy.SlowWindows)
                {
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.SlowSource, "download.transfer.slow-source");
                }
            }
        }

        return null;
    }
}

internal sealed record DownloadTransferWatchdog(
    TimeSpan PollInterval,
    TimeSpan StartupGrace,
    TimeSpan Window,
    long MinimumBytesPerSecond,
    int SlowWindows)
{
    public static DownloadTransferWatchdog Default { get; } = new(
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10), 16 * 1024, 2);
}
