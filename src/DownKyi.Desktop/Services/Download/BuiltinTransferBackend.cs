using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Utils;
using Downloader;
using Downloader.Exceptions;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class BuiltinTransferBackend : ITransferBackend
{
    private readonly ISettingsStore _settingsStore;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly ILogger<BuiltinTransferBackend> _logger;

    public BuiltinTransferBackend(
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        ILogger<BuiltinTransferBackend> logger)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "built-in";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Urls.Count != 1)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.single-address-required");
        }

        var url = request.Urls[0];
        var path = request.Directory;
        var localFileName = request.FileName;
        var expectedBytes = request.ExpectedBytes;
        var network = _settingsStore.Current.Network;
        var requestConfiguration = new RequestConfiguration
        {
            Headers = new WebHeaderCollection
            {
                { "cookie", LoginHelper.GetLoginInfoCookiesString() }
            },
            UserAgent = network.UserAgent,
            Referer = "https://www.bilibili.com"
        };
        if (network.IsHttpProxy == AllowStatus.Yes)
        {
            requestConfiguration.Proxy = new WebProxy(
                network.HttpProxy,
                network.HttpProxyListenPort);
        }

        var split = network.Split;
        var configuration = new DownloadConfiguration
        {
            ChunkCount = split,
            RequestConfiguration = requestConfiguration,
            ParallelDownload = true,
            ParallelCount = split,
            MaxTryAgainOnFailure = 0,
            MaximumMemoryBufferBytes = 50 * 1024 * 1024,
            EnableAutoResumeDownload = true,
            ClearPackageOnCompletionWithFailure = false,
            FileExistPolicy = FileExistPolicy.IgnoreDownload
        };

        var targetFile = Path.Combine(path, localFileName);
        var totalBytesToReceive = expectedBytes;
        var receivedBytes = 0L;
        var progressUpdater = new DownloadProgressUiUpdater(
            TimeProvider.System,
            DownloadProgressUiUpdater.DefaultMinimumInterval);
        DownloadProgress? lastProgress = null;
        Exception? reportedError = null;
        var reportedCanceled = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticLogger.LogBuiltInTaskStart(
            Name,
            localFileName,
            request.Urls.Count,
            configuration.ChunkCount,
            configuration.ParallelCount,
            network);

        using var downloader = new Downloader.DownloadService(configuration);
        downloader.DownloadStarted += (_, args) =>
        {
            if (args.TotalBytesToReceive > 0)
            {
                totalBytesToReceive = (long)args.TotalBytesToReceive;
            }
        };
        downloader.DownloadProgressChanged += (_, args) =>
        {
            receivedBytes = (long)Math.Max(0, args.ReceivedBytesSize);
            if (args.TotalBytesToReceive > 0)
            {
                totalBytesToReceive = (long)args.TotalBytesToReceive;
            }

            var speed = (long)args.BytesPerSecondSpeed;
            if (progressUpdater.TryCreate(
                    args.ProgressPercentage,
                    args.ReceivedBytesSize,
                    args.TotalBytesToReceive,
                    speed,
                    out var progress))
            {
                lastProgress = progress;
                request.PublishProgress(progress);
                _diagnosticLogger.LogSpeed(
                    Name,
                    localFileName,
                    args.ReceivedBytesSize,
                    args.TotalBytesToReceive,
                    speed);
            }
        };
        downloader.DownloadFileCompleted += (_, args) =>
        {
            reportedError = args.Error;
            reportedCanceled = args.Cancelled;
            if (args.Error != null)
            {
                _logger.LogWarningMessage(
                    $"Built-in download completion reported an error; " +
                    $"type={args.Error.GetType().Name}.");
            }

            request.SetBuiltinDownloadService(null);
            completion.TrySetResult();
        };

        request.SetBuiltinDownloadService(downloader);
        var transferTask = downloader.DownloadFileTaskAsync(
            url,
            targetFile,
            request.CancellationToken);
        Exception? transferError = null;
        try
        {
            while (!completion.Task.IsCompleted && !transferTask.IsCompleted)
            {
                if (request.IsPauseRequested())
                {
                    downloader.Pause();
                    downloader.CancelAsync();
                    request.SetBuiltinDownloadService(null);
                    if (lastProgress != null)
                    {
                        await request.PersistProgressAsync(lastProgress, CancellationToken.None)
                            .ConfigureAwait(true);
                    }

                    throw new OperationCanceledException("Download was paused.");
                }

                request.EnsureActive();

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    request.CancellationToken).ConfigureAwait(true);
            }

            await transferTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            downloader.CancelAsync();
            request.SetBuiltinDownloadService(null);
            try
            {
                await transferTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebugMessage("Built-in transfer observed expected cancellation.");
            }

            throw;
        }
        catch (OperationCanceledException exception)
        {
            downloader.CancelAsync();
            try
            {
                await transferTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebugMessage("Built-in transfer teardown was canceled.");
            }

            if (request.IsPauseRequested())
            {
                return DownloadTransferResult.Paused();
            }

            request.EnsureActive();
            transferError = exception;
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
            transferError = exception;
        }
        finally
        {
            request.SetBuiltinDownloadService(null);
        }

        completion.TrySetResult();
        if (lastProgress != null)
        {
            await request.PersistProgressAsync(lastProgress, CancellationToken.None)
                .ConfigureAwait(true);
        }

        if (request.IsPauseRequested())
        {
            return DownloadTransferResult.Paused();
        }

        if (transferError == null &&
            reportedError == null &&
            !reportedCanceled &&
            IsDownloadedMediaFileUsable(
                targetFile,
                expectedBytes,
                receivedBytes,
                totalBytesToReceive))
        {
            return DownloadTransferResult.Succeeded();
        }

        return ClassifyFailure(transferError ?? reportedError, reportedCanceled);
    }

    public void Dispose()
    {
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

        if (FindException<HttpRequestException>(exception) is { } httpException)
        {
            return httpException.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.RateLimited,
                    "download.transfer.http-429"),
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
                    $"download.transfer.http-{(int)httpException.StatusCode.Value}"),
                null => DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.TransientNetwork,
                    "download.transfer.network"),
                _ => DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.Permanent,
                    $"download.transfer.http-{(int)httpException.StatusCode.Value}")
            };
        }

        if (FindException<TimeoutException>(exception) != null ||
            FindException<OperationCanceledException>(exception) != null ||
            FindException<SocketException>(exception) != null ||
            FindException<HttpIOException>(exception) != null ||
            FindException<IncompleteDownloadException>(exception) != null ||
            reportedCanceled)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.timeout");
        }

        if (FindException<UnauthorizedAccessException>(exception) != null ||
            FindException<DirectoryNotFoundException>(exception) != null ||
            FindException<DriveNotFoundException>(exception) != null ||
            FindException<PathTooLongException>(exception) != null ||
            FindException<IOException>(exception) != null)
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
        var result = DownloadFileIntegrity.Check(file, expectedBytes, receivedBytes, totalBytesToReceive);
        if (!result.IsUsable)
        {
            _logger.LogInformationMessage(result.Reason ?? "Downloaded media file is not usable.");
        }

        return result.IsUsable;
    }

}
