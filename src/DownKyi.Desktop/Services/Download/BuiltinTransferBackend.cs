using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class BuiltinTransferBackend : ITransferBackend
{
    private readonly ISettingsStore _settingsStore;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly ILogger<BuiltinTransferBackend> _logger;
    private readonly DownloadBandwidthLimiter _bandwidthLimiter;
    private readonly Func<Uri?, IDownloadAddressResolver> _addressResolverFactory;

    public BuiltinTransferBackend(
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        ILogger<BuiltinTransferBackend> logger)
        : this(
            settingsStore,
            diagnosticLogger,
            logger,
            static proxyAddress => DownloadAddressResolver.Create(proxyAddress))
    {
    }

    internal BuiltinTransferBackend(
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        ILogger<BuiltinTransferBackend> logger,
        Func<Uri?, IDownloadAddressResolver> addressResolverFactory)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _addressResolverFactory = addressResolverFactory
            ?? throw new ArgumentNullException(nameof(addressResolverFactory));
        _bandwidthLimiter = new DownloadBandwidthLimiter(
            () =>
            {
                var network = _settingsStore.Current.Network;
                return DownloadBandwidthLimits.FromKilobytesPerSecond(
                    network.MaxOverallDownloadLimit,
                    network.MaxDownloadLimit);
            },
            TimeProvider.System);
    }

    public string Name => "built-in";

    public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Urls.Count != 1)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.single-address-required");
        }

        var network = _settingsStore.Current.Network;
        var targetFile = Path.Combine(request.Directory, request.FileName);
        var progressUpdater = new DownloadProgressUiUpdater(
            TimeProvider.System,
            DownloadProgressUiUpdater.DefaultMinimumInterval);
        var speedSampler = new BuiltinDownloadSpeedSampler(TimeProvider.System);
        var progressSync = new Lock();
        DownloadProgress? lastProgress = null;

        void ReportProgress(long received, long total)
        {
            lock (progressSync)
            {
                var percentage = total <= 0 ? 0 : (double)received / total * 100;
                var speed = speedSampler.Sample(received);
                if (!progressUpdater.TryCreate(
                        percentage,
                        received,
                        total,
                        speed,
                        out var progress))
                {
                    return;
                }

                lastProgress = progress;
                request.PublishProgress(progress);
                _diagnosticLogger.LogSpeed(
                    Name,
                    request.FileName,
                    received,
                    total,
                    speed);
            }
        }

        try
        {
            if (BuiltinRangeDownloader.TryGetCompletedTarget(
                    targetFile,
                    request.ExpectedBytes,
                    out var completedTarget))
            {
                ReportProgress(completedTarget.ReceivedBytes, completedTarget.TotalBytes);
                return DownloadTransferResult.Succeeded();
            }

            var proxyAddress = ResolveProxyAddress(network);
            using var resolver = _addressResolverFactory(proxyAddress)
                ?? throw new InvalidOperationException(
                    "The download address resolver factory returned no resolver.");
            var resolution = await resolver.ResolveAsync(
                request.Urls[0],
                network.UserAgent,
                LoginHelper.GetLoginInfoCookiesString(),
                request.CancellationToken).ConfigureAwait(true);
            DownloadAddressAccepted accepted;
            switch (resolution)
            {
                case DownloadAddressAccepted value:
                    accepted = value;
                    break;
                case DownloadAddressCandidateRejected rejected:
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.CandidateRejected,
                        rejected.ErrorCode);
                case DownloadAddressTimedOut:
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.TransientNetwork,
                        "download.transfer.timeout");
                case DownloadAddressTransportFailure:
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.TransientNetwork,
                        "download.transfer.network");
                default:
                    throw new InvalidOperationException(
                        "The download address resolver returned an unknown outcome.");
            }

            var resolvedAddress = accepted.Address;
            var headers = accepted.Headers;
            using var handler = CreateHttpHandler(network, proxyAddress);
            var bandwidthLease = _bandwidthLimiter.CreateTransferLease();
            using var downloader = new BuiltinRangeDownloader(
                handler,
                resolvedAddress,
                headers,
                network.Split,
                ReportProgress,
                _logger,
                disposeHandler: false,
                bandwidthLimiter: bandwidthLease,
                planCreated: chunkCount => _diagnosticLogger.LogBuiltInTaskStart(
                    Name,
                    request.FileName,
                    request.Urls.Count,
                    chunkCount,
                    network.Split,
                    network));
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
            var transferTask = downloader.DownloadAsync(
                targetFile,
                request.ExpectedBytes,
                transferCancellation.Token);

            while (!transferTask.IsCompleted)
            {
                if (request.IsPauseRequested())
                {
                    await transferCancellation.CancelAsync().ConfigureAwait(true);
                    break;
                }

                request.EnsureActive();
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    request.CancellationToken).ConfigureAwait(true);
            }

            var result = await transferTask.ConfigureAwait(true);
            if (IsDownloadedMediaFileUsable(
                    targetFile,
                    request.ExpectedBytes,
                    result.ReceivedBytes,
                    result.TotalBytes))
            {
                return DownloadTransferResult.Succeeded();
            }

            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.InvalidMedia,
                "download.transfer.invalid-media");
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (request.IsPauseRequested())
        {
            return DownloadTransferResult.Paused();
        }
        catch (Exception exception) when (exception is IOException
            or HttpRequestException
            or InvalidOperationException
            or TimeoutException
            or UnauthorizedAccessException
            or AggregateException)
        {
            _logger.LogWarningMessage(
                $"Built-in transfer failed; type={exception.GetType().Name}.");
            return ClassifyFailure(exception, reportedCanceled: false);
        }
        finally
        {
            if (lastProgress != null)
            {
                await request.PersistProgressAsync(lastProgress, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
    }

    internal static DownloadTransferResult ClassifyFailure(
        Exception? exception,
        bool reportedCanceled)
    {
        if (TlsFailureClassifier.TryClassify(exception, out var tlsErrorCode))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Tls,
                tlsErrorCode);
        }

        if (TlsFailureClassifier.IsSecureConnectionFailure(exception))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.network");
        }

        if (FindException<BuiltinResumeRejectedException>(exception) != null)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ResumeRejected,
                "download.transfer.resume-rejected");
        }

        if (FindException<BuiltinHttpStatusException>(exception) is { } statusException)
        {
            return ClassifyHttpStatus(statusException.StatusCode, statusException.RetryAfter);
        }

        if (FindException<HttpRequestException>(exception) is { } httpException)
        {
            return ClassifyHttpStatus(httpException.StatusCode, retryAfter: null);
        }

        if (FindException<TimeoutException>(exception) != null
            || FindException<OperationCanceledException>(exception) != null
            || FindException<SocketException>(exception) != null
            || FindException<HttpIOException>(exception) != null
            || reportedCanceled)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout");
        }

        if (FindException<UnauthorizedAccessException>(exception) != null
            || FindException<DirectoryNotFoundException>(exception) != null
            || FindException<DriveNotFoundException>(exception) != null
            || FindException<PathTooLongException>(exception) != null
            || FindException<IOException>(exception) != null)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Disk,
                "download.transfer.disk");
        }

        return DownloadTransferResult.Failed(
            exception == null
                ? DownloadTransferFailureKind.InvalidMedia
                : DownloadTransferFailureKind.Permanent,
            exception == null
                ? "download.transfer.invalid-media"
                : "download.transfer.permanent");
    }

    private static DownloadTransferResult ClassifyHttpStatus(
        HttpStatusCode? statusCode,
        TimeSpan? retryAfter)
    {
        return statusCode switch
        {
            HttpStatusCode.TooManyRequests => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.RateLimited,
                "download.transfer.http-429",
                retryAfter),
            HttpStatusCode.Unauthorized => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.http-401"),
            HttpStatusCode.Forbidden => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "download.transfer.http-403"),
            HttpStatusCode.NotFound => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                "download.transfer.http-404"),
            HttpStatusCode.RequestTimeout => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.http-408"),
            >= HttpStatusCode.InternalServerError => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.http-server-error"),
            null => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.network"),
            _ => DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.http-client-error")
        };
    }

    private static SocketsHttpHandler CreateHttpHandler(
        NetworkApplicationSettings network,
        Uri? proxyAddress)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = network.Split,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = proxyAddress != null
        };
        if (proxyAddress != null)
        {
            handler.Proxy = new WebProxy(proxyAddress)
            {
                BypassProxyOnLocal = false
            };
        }

        return handler;
    }

    private static Uri? ResolveProxyAddress(NetworkApplicationSettings network)
    {
        if (network.IsHttpProxy != AllowStatus.Yes)
        {
            return null;
        }

        try
        {
            return new UriBuilder(
                Uri.UriSchemeHttp,
                network.HttpProxy,
                network.HttpProxyListenPort).Uri;
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException(
                "The built-in download proxy address is invalid.",
                exception);
        }
    }

    private static TException? FindException<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception != null)
        {
            if (exception is TException match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private bool IsDownloadedMediaFileUsable(
        string? file,
        long expectedBytes = 0,
        long receivedBytes = 0,
        long totalBytesToReceive = 0)
    {
        var result = DownloadFileIntegrity.Check(
            file,
            expectedBytes,
            receivedBytes,
            totalBytesToReceive);
        if (!result.IsUsable)
        {
            _logger.LogInformationMessage(result.Reason ?? "Downloaded media file is not usable.");
        }

        return result.IsUsable;
    }

    private sealed class BuiltinDownloadSpeedSampler(TimeProvider timeProvider)
    {
        private readonly TimeProvider _timeProvider = timeProvider;
        private readonly Lock _sync = new();
        private long _lastBytes;
        private DateTimeOffset? _lastSampleAt;
        private long _speed;

        public long Sample(long receivedBytes)
        {
            var now = _timeProvider.GetUtcNow();
            lock (_sync)
            {
                if (_lastSampleAt is not { } lastSampleAt)
                {
                    _lastSampleAt = now;
                    _lastBytes = receivedBytes;
                    return 0;
                }

                var elapsed = now - lastSampleAt;
                if (elapsed < TimeSpan.FromMilliseconds(100))
                {
                    return _speed;
                }

                var bytes = Math.Max(0, receivedBytes - _lastBytes);
                _speed = elapsed.TotalSeconds <= 0
                    ? 0
                    : checked((long)Math.Min(bytes / elapsed.TotalSeconds, long.MaxValue));
                _lastSampleAt = now;
                _lastBytes = receivedBytes;
                return _speed;
            }
        }
    }
}
