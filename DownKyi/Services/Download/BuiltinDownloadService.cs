using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using Downloader;
using Microsoft.Extensions.Logging;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.Services.Download;

internal sealed class BuiltinDownloadService : DownloadService, IDownloadService
{
    public BuiltinDownloadService(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IDialogService? dialogService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        ILogger<BuiltinDownloadService> logger)
        : base(
            downloadLists,
            downloadStorageService,
            dialogService,
            uiDispatcher,
            settingsStore,
            diagnosticLogger,
            ffmpegProcessor,
            logger)
    {
        Tag = nameof(BuiltinDownloadService);
    }

    public Task EndAsync()
    {
        return BaseEndTask();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BaseStart();
        return Task.CompletedTask;
    }

    protected override void Pause(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        CancellationToken?.ThrowIfCancellationRequested();
        downloading.DownloadStatusTitle = DictionaryResource.GetString("Pausing");
        if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
        {
            throw new OperationCanceledException("Download was paused.");
        }

        if (!DownloadingList.Contains(downloading))
        {
            throw new OperationCanceledException("Download was deleted.");
        }
    }

    protected override async Task<DownloadTransferOutcome> TransferAsync(
        DownloadingItem downloading,
        IReadOnlyList<string> urls,
        string path,
        string localFileName,
        long expectedBytes)
    {
        var network = Settings.Network;
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
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DiagnosticLogger.LogBuiltInTaskStart(
                Tag,
                localFileName,
                urls.Count,
                configuration.ChunkCount,
                configuration.ParallelCount);

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

                downloading.Progress = (float)args.ProgressPercentage;
                downloading.DownloadingFileSize = $"{Format.FormatFileSize(args.ReceivedBytesSize)}/{Format.FormatFileSize(args.TotalBytesToReceive)}";
                var speed = (long)args.BytesPerSecondSpeed;
                downloading.SpeedDisplay = Format.FormatSpeedWithBandwidth(speed);
                DiagnosticLogger.LogSpeed(
                    Tag,
                    localFileName,
                    args.ReceivedBytesSize,
                    args.TotalBytesToReceive,
                    speed);
                downloading.Downloading.MaxSpeed = Math.Max(downloading.Downloading.MaxSpeed, speed);
            };
            downloader.DownloadFileCompleted += (_, args) =>
            {
                if (args.Error != null)
                {
                    Logger.LogErrorMessage("Built-in download completion reported an error.", args.Error);
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
                CancellationToken.GetValueOrDefault());
            while (!completion.Task.IsCompleted && !transferTask.IsCompleted)
            {
                CancellationToken?.ThrowIfCancellationRequested();
                if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
                {
                    downloader.Pause();
                    downloader.CancelAsync();
                    downloading.DownloadService = null;
                    Pause(downloading);
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
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
            Logger.LogInformationMessage("Built-in transfer was incomplete; trying a backup endpoint.");
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
            Logger.LogErrorMessage("Built-in transfer failed.", e);
            return false;
        }
    }
}
