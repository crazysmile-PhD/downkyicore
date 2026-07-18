using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Utils;
using Downloader;
using Microsoft.Extensions.Logging;
using DownloadStatus = DownKyi.Models.DownloadStatus;

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

    public async Task<DownloadTransferOutcome> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var downloading = request.Download;
        var urls = request.Urls;
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
            MaximumMemoryBufferBytes = 50 * 1024 * 1024,
            EnableAutoResumeDownload = true,
            ClearPackageOnCompletionWithFailure = false,
            FileExistPolicy = FileExistPolicy.IgnoreDownload
        };

        foreach (var url in urls)
        {
            var targetFile = Path.Combine(path, localFileName);
            var totalBytesToReceive = expectedBytes;
            var receivedBytes = 0L;
            var progressUpdater = new DownloadProgressUiUpdater(
                TimeProvider.System,
                DownloadProgressUiUpdater.DefaultMinimumInterval);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _diagnosticLogger.LogBuiltInTaskStart(
                Name,
                localFileName,
                urls.Count,
                configuration.ChunkCount,
                configuration.ParallelCount,
                network);

            var downloader = new Downloader.DownloadService(configuration);
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
                if (progressUpdater.TryUpdate(
                        downloading,
                        args.ProgressPercentage,
                        args.ReceivedBytesSize,
                        args.TotalBytesToReceive,
                        speed))
                {
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
                if (args.Error != null)
                {
                    _logger.LogErrorMessage("Built-in download completion reported an error.", args.Error);
                }

                var succeeded = !args.Cancelled &&
                                args.Error == null &&
                                IsDownloadedMediaFileUsable(
                                    targetFile,
                                    expectedBytes,
                                    receivedBytes,
                                    totalBytesToReceive);
                downloading.DownloadService = null;
                completion.TrySetResult(succeeded);
            };

            downloading.DownloadService = downloader;
            var transferTask = RunDownloadAsync(
                downloader,
                url,
                targetFile,
                request.CancellationToken);
            while (!completion.Task.IsCompleted && !transferTask.IsCompleted)
            {
                request.EnsureActive();
                if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
                {
                    downloader.Pause();
                    downloader.CancelAsync();
                    downloading.DownloadService = null;
                    throw new OperationCanceledException("Download was paused.");
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    request.CancellationToken).ConfigureAwait(true);
            }

            var taskSucceeded = await transferTask.ConfigureAwait(true);
            completion.TrySetResult(taskSucceeded && IsDownloadedMediaFileUsable(
                targetFile,
                expectedBytes,
                receivedBytes,
                totalBytesToReceive));
            if (await completion.Task.ConfigureAwait(true))
            {
                return DownloadTransferOutcome.Succeeded;
            }

            DeleteInvalidDownloadedMediaFile(targetFile);
            _logger.LogInformationMessage("Built-in transfer was incomplete; trying a backup endpoint.");
        }

        return DownloadTransferOutcome.Failed;
    }

    private async Task<bool> RunDownloadAsync(
        Downloader.DownloadService downloader,
        string url,
        string targetFile,
        CancellationToken cancellationToken)
    {
        try
        {
            await downloader.DownloadFileTaskAsync(url, targetFile, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogErrorMessage("Built-in transfer failed.", e);
            return false;
        }
    }

    public void Dispose()
    {
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

    private void DeleteInvalidDownloadedMediaFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        foreach (var path in new[] { file, $"{file}.aria2", $"{file}.download" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException e)
            {
                _logger.LogDebugMessage($"Delete invalid media file failed: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogDebugMessage($"Delete invalid media file was denied: {e.Message}");
            }
        }
    }
}
