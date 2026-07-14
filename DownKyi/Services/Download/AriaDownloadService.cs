using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal class AriaDownloadService : DownloadService, IDownloadService
{
    private readonly bool _ownsAriaServer;

    public AriaDownloadService(
        ImmutableObservableCollection<DownloadingItem> downloadingList,
        ImmutableObservableCollection<DownloadedItem> downloadedList,
        IDialogService? dialogService)
        : this(downloadingList, downloadedList, dialogService, ownsAriaServer: true)
    {
    }

    protected AriaDownloadService(
        ImmutableObservableCollection<DownloadingItem> downloadingList,
        ImmutableObservableCollection<DownloadedItem> downloadedList,
        IDialogService? dialogService,
        bool ownsAriaServer)
        : base(downloadingList, downloadedList, dialogService)
    {
        _ownsAriaServer = ownsAriaServer;
        Tag = ownsAriaServer ? nameof(AriaDownloadService) : nameof(CustomAriaDownloadService);
    }

    public async Task EndAsync()
    {
        await BaseEndTask().ConfigureAwait(true);
        if (_ownsAriaServer)
        {
            await CloseAriaServerAsync().ConfigureAwait(true);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ownsAriaServer)
        {
            AriaClient.SetToken();
            AriaClient.SetHost();
        }
        else
        {
            AriaClient.SetToken(SettingsManager.Instance.GetAriaToken());
            AriaClient.SetHost(SettingsManager.Instance.GetAriaHost());
        }

        AriaClient.SetListenPort(SettingsManager.Instance.GetAriaListenPort());
        if (_ownsAriaServer)
        {
            await StartAriaServerAsync(cancellationToken).ConfigureAwait(true);
        }

        BaseStart();
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
        _ = expectedBytes;
        var activeGid = await EnsureAriaTaskAsync(
            downloading,
            urls,
            path,
            localFileName).ConfigureAwait(true);
        if (activeGid == null)
        {
            return DownloadTransferOutcome.Failed;
        }

        DownloadDiagnosticLogger.LogAriaTaskStart(Tag, activeGid, urls.Count);
        var ariaManager = new AriaManager();
        ariaManager.TellStatus += AriaTellStatus;
        var result = await ariaManager.GetDownloadStatusAsync(
            activeGid,
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
                {
                    await AriaClient.PauseAsync(activeGid).ConfigureAwait(false);
                    Pause(downloading);
                }
            },
            CancellationToken.GetValueOrDefault()).ConfigureAwait(true);

        return result switch
        {
            DownloadResult.SUCCESS => DownloadTransferOutcome.Succeeded,
            DownloadResult.ABORT => DownloadTransferOutcome.Failed,
            _ => DownloadTransferOutcome.Failed
        };
    }

    private async Task<string?> EnsureAriaTaskAsync(
        DownloadingItem downloading,
        IReadOnlyList<string> urls,
        string path,
        string localFileName)
    {
        var gid = downloading.Downloading.Gid;
        if (!string.IsNullOrWhiteSpace(gid))
        {
            var status = await AriaClient.TellStatus(gid).ConfigureAwait(true);
            if (status?.Result == null ||
                status.Error.Message.Contains("is not found", StringComparison.OrdinalIgnoreCase))
            {
                gid = null;
                downloading.Downloading.Gid = null;
                await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
            }
        }

        if (gid == null)
        {
            var option = new AriaSendOption
            {
                Dir = path,
                Out = localFileName,
                Continue = "true",
                AllowOverwrite = "true",
                AutoFileRenaming = "false",
                UserAgent = SettingsManager.Instance.GetUserAgent(),
                Split = SettingsManager.Instance.GetAriaSplit().ToString(CultureInfo.InvariantCulture),
                MaxConnectionPerServer = SettingsManager.Instance.GetAriaMaxConnectionPerServer()
                    .ToString(CultureInfo.InvariantCulture),
                MinSplitSize = $"{SettingsManager.Instance.GetAriaMinSplitSize()}M"
            };
            if (SettingsManager.Instance.GetIsAriaHttpProxy() == AllowStatus.Yes)
            {
                option.HttpProxy = $"http://{SettingsManager.Instance.GetAriaHttpProxy()}:{SettingsManager.Instance.GetAriaHttpProxyListenPort()}";
            }

            var added = await AriaClient.AddUriAsync(urls.ToList(), option).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(added?.Result))
            {
                return null;
            }

            gid = added.Result;
            downloading.Downloading.Gid = gid;
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }
        else
        {
            await AriaClient.UnpauseAsync(gid).ConfigureAwait(true);
        }

        return gid;
    }

    private async Task StartAriaServerAsync(CancellationToken cancellationToken)
    {
        var config = new AriaConfig
        {
            ListenPort = SettingsManager.Instance.GetAriaListenPort(),
            Token = "downkyi",
            LogLevel = SettingsManager.Instance.GetAriaLogLevel(),
            MaxConcurrentDownloads = SettingsManager.Instance.GetMaxCurrentDownloads(),
            MaxConnectionPerServer = SettingsManager.Instance.GetAriaMaxConnectionPerServer(),
            Split = SettingsManager.Instance.GetAriaSplit(),
            MinSplitSize = SettingsManager.Instance.GetAriaMinSplitSize(),
            MaxOverallDownloadLimit = SettingsManager.Instance.GetAriaMaxOverallDownloadLimit() * 1024L,
            MaxDownloadLimit = SettingsManager.Instance.GetAriaMaxDownloadLimit() * 1024L,
            ContinueDownload = true,
            FileAllocation = SettingsManager.Instance.GetAriaFileAllocation(),
            Headers =
            [
                $"Cookie: {LoginHelper.GetLoginInfoCookiesString()}",
                "Origin: https://www.bilibili.com",
                "Referer: https://www.bilibili.com",
                $"User-Agent: {SettingsManager.Instance.GetUserAgent()}"
            ]
        };
        DownloadDiagnosticLogger.LogAriaServerConfig(Tag, config);

        var errors = new StringBuilder();
        await AriaServer.StartServerAsync(config, output =>
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                errors.AppendLine(output);
            }
        }).ConfigureAwait(true);

        var message = errors.ToString();
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            var alertService = new AlertService(DialogService);
            await alertService.ShowMessage(
                SystemIcon.Instance().Error,
                $"Aria2 {DictionaryResource.GetString("Error")}",
                message,
                1).ConfigureAwait(true);
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await AriaClient.GetGlobalOptionAsync().ConfigureAwait(true) != null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task CloseAriaServerAsync()
    {
        try
        {
            await AriaClient.PauseAllAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is TimeoutException or HttpRequestException or IOException
            or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(Tag, e);
        }

        if (!await AriaServer.CloseServerAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true))
        {
            await AriaServer.ForceCloseServerAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
    }

    private void AriaTellStatus(object? sender, AriaProgressEventArgs eventArgs)
    {
        var video = DownloadingList.FirstOrDefault(item => item.Downloading.Gid == eventArgs.Gid);
        if (video == null)
        {
            return;
        }

        var percent = eventArgs.TotalLength == 0
            ? 0
            : (float)eventArgs.CompletedLength / eventArgs.TotalLength * 100;
        if (Math.Abs(percent - video.Progress) < 0.01)
        {
            return;
        }

        video.Progress = percent;
        video.DownloadingFileSize = $"{Format.FormatFileSize(eventArgs.CompletedLength)}/{Format.FormatFileSize(eventArgs.TotalLength)}";
        video.SpeedDisplay = Format.FormatSpeedWithBandwidth(eventArgs.Speed);
        DownloadDiagnosticLogger.LogSpeed(
            Tag,
            eventArgs.Gid,
            eventArgs.CompletedLength,
            eventArgs.TotalLength,
            eventArgs.Speed);
        video.Downloading.MaxSpeed = Math.Max(video.Downloading.MaxSpeed, eventArgs.Speed);
    }
}
