using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.VideoStream.Models;
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

public class AriaDownloadService : DownloadService, IDownloadService
{
    public AriaDownloadService(
        ImmutableObservableCollection<DownloadingItem> downloadingList,
        ImmutableObservableCollection<DownloadedItem> downloadedList,
        IDialogService? dialogService) :
        base(downloadingList, downloadedList, dialogService)
    {
        Tag = "AriaDownloadService";
    }

    #region 音视频

    /// <summary>
    /// 下载音频，返回下载文件路径
    /// </summary>
    /// <param name="downloading"></param>
    /// <returns></returns>
    public override string? DownloadAudio(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        var downloadAudio = BaseDownloadAudio(downloading);

        return DownloadVideo(downloading, downloadAudio);
    }

    /// <summary>
    /// 下载视频，返回下载文件路径
    /// </summary>
    /// <param name="downloading"></param>
    /// <returns></returns>
    public override string? DownloadVideo(DownloadingItem downloading)
    {
        var downloadVideo = BaseDownloadVideo(downloading);

        return DownloadVideo(downloading, downloadVideo);
    }

    /// <summary>
    /// 将下载音频和视频的函数中相同代码抽象出来
    /// </summary>
    /// <param name="downloading"></param>
    /// <param name="downloadVideo"></param>
    /// <returns></returns>
    private string? DownloadVideo(DownloadingItem downloading, VideoPlayUrlBasic? downloadVideo)
    {
        if (downloadVideo == null)
        {
            return null;
        }

        EnsureDownloadIsActive(downloading);

        return DownloadVideo(downloading, new PlayUrlDashVideo
        {
            Id = downloadVideo.Id,
            Codecs = downloadVideo.Codecs,
            BaseUrl = downloadVideo.BaseUrl,
            BackupUrl = downloadVideo.BackupUrl,
            ExpectedSize = downloadVideo.ExpectedSize
        });
    }

    private string? DownloadVideo(DownloadingItem downloading, PlayUrlDashVideo? downloadVideo)
    {
        // 如果为空，说明没有匹配到可下载的音频视频
        if (downloadVideo == null)
        {
            return null;
        }

        // 下载链接
        var urls = new List<string>();
        if (downloadVideo.BaseUrl != null)
        {
            urls.Add(downloadVideo.BaseUrl);
        }

        if (downloadVideo.BackupUrl != null)
        {
            urls.AddRange(downloadVideo.BackupUrl);
        }

        // 路径
        downloading.DownloadBase.FilePath = downloading.DownloadBase.FilePath.Replace("\\", "/");
        var temp = downloading.DownloadBase.FilePath.Split('/');
        //string path = downloading.DownloadBase.FilePath.Replace(temp[temp.Length - 1], "");
        var path = downloading.DownloadBase.FilePath.TrimEnd(temp[temp.Length - 1].ToCharArray());

        // 下载文件名
        var fileName = Guid.NewGuid().ToString("N");
        var key = $"{downloadVideo.Id}_{downloadVideo.Codecs}";

        // 老版本数据库没有这一项，会变成null
        if (downloading.Downloading.DownloadedFiles == null)
        {
            downloading.Downloading.DownloadedFiles = new List<string>();
        }

        if (downloading.Downloading.DownloadFiles.TryGetValue(key, out var existingFileName))
        {
            // 如果存在，表示下载过，
            // 则继续使用上次下载的文件名
            fileName = existingFileName;

            // 还要检查一下文件有没有被人删掉，删掉的话重新下载
            // 如果下载视频之后音频文件被人删了。此时gid还是视频的，会下错文件
            var cachedFile = Path.Combine(path, fileName);
            if (downloading.Downloading.DownloadedFiles.Contains(key) &&
                IsDownloadedMediaFileUsable(cachedFile, downloadVideo.ExpectedSize))
            {
                return cachedFile;
            }

            if (downloading.Downloading.DownloadedFiles.Remove(key))
            {
                DeleteInvalidDownloadedMediaFile(cachedFile);
                PersistDownloadingState(downloading);
            }
        }
        else if (downloading.Downloading.DownloadFiles.TryAdd(key, fileName))
        {
            // Gid最好能是每个文件单独存储，现在复用有可能会混
            // 不过好消息是下载是按固定顺序的，而且下载了两个音频会混流不过
            downloading.Downloading.Gid = null;
            PersistDownloadingState(downloading);
        }

        // 启用https
        var useSSL = SettingsManager.Instance.GetUseSsl();
        if (useSSL == AllowStatus.Yes)
        {
            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                if (url.StartsWith("http://"))
                {
                    urls[i] = url.Replace("http://", "https://");
                }
            }
        }
        else
        {
            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                if (url.StartsWith("https://"))
                {
                    urls[i] = url.Replace("https://", "http://");
                }
            }
        }

        // 开始下载
        var downloadStatus = DownloadByAria(downloading, urls, path, fileName);
        var targetFile = Path.Combine(path, fileName);
        switch (downloadStatus)
        {
            case DownloadResult.SUCCESS:
                if (IsDownloadedMediaFileUsable(targetFile, downloadVideo.ExpectedSize))
                {
                    downloading.Downloading.DownloadedFiles.Add(key);
                    downloading.Downloading.Gid = null;
                    PersistDownloadingState(downloading);
                    return targetFile;
                }

                downloading.Downloading.Gid = null;
                PersistDownloadingState(downloading);
                DeleteInvalidDownloadedMediaFile(targetFile);
                return NullMark;
            case DownloadResult.FAILED:
            case DownloadResult.ABORT:
            default:
                return NullMark;
        }
    }

    #endregion

    /// <summary>
    /// 下载封面
    /// </summary>
    /// <param name="downloading"></param>
    /// <param name="coverUrl"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public override string? DownloadCover(DownloadingItem downloading, string? coverUrl, string fileName)
    {
        return BaseDownloadCover(downloading, coverUrl, fileName);
    }

    /// <summary>
    /// 下载弹幕
    /// </summary>
    /// <param name="downloading"></param>
    public override string DownloadDanmaku(DownloadingItem downloading)
    {
        return BaseDownloadDanmaku(downloading);
    }

    /// <summary>
    /// 下载字幕
    /// </summary>
    /// <param name="downloading"></param>
    public override IReadOnlyList<string> DownloadSubtitle(DownloadingItem downloading)
    {
        return BaseDownloadSubtitle(downloading);
    }

    /// <summary>
    /// 混流音频和视频
    /// </summary>
    /// <param name="downloading"></param>
    /// <param name="audioUid"></param>
    /// <param name="videoUid"></param>
    /// <returns></returns>
    public override string? MixedFlow(DownloadingItem downloading, string? audioUid, string? videoUid)
    {
        if (videoUid == NullMark)
        {
            return null;
        }

        return BaseMixedFlow(downloading, audioUid, videoUid);
    }

    /// <summary>
    /// 解析视频流的下载链接
    /// </summary>
    /// <param name="downloading"></param>
    public override void Parse(DownloadingItem downloading)
    {
        BaseParse(downloading);
    }

    /// <summary>
    /// 停止下载服务(转换await和Task.Wait两种调用形式)
    /// </summary>
    private async Task EndTask()
    {
        // 停止基本任务
        await BaseEndTask().ConfigureAwait(true);

        // 关闭Aria服务器
        await CloseAriaServer().ConfigureAwait(true);
    }

    /// <summary>
    /// 停止下载服务
    /// </summary>
    public Task EndAsync()
    {
        return EndTask();
    }

    /// <summary>
    /// 启动下载服务
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 设置aria token
        AriaClient.SetToken();
        // 设置aria host
        AriaClient.SetHost();
        // 设置aria listenPort
        AriaClient.SetListenPort(SettingsManager.Instance.GetAriaListenPort());

        // 启动Aria服务器
        await StartAriaServerAsync(cancellationToken).ConfigureAwait(true);

        // 启动基本服务
        BaseStart();
    }

    /// <summary>
    /// 强制暂停
    /// </summary>
    /// <param name="downloading"></param>
    /// <exception cref="OperationCanceledException"></exception>
    protected override void Pause(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        CancellationToken?.ThrowIfCancellationRequested();

        downloading.DownloadStatusTitle = DictionaryResource.GetString("Pausing");

        // 是否存在
        var isExist = IsExist(downloading);
        if (!isExist.Result)
        {
            throw new OperationCanceledException("Task is deleted");
        }

        if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
        {
            throw new OperationCanceledException("Stop thread by pause");
        }
    }

    /// <summary>
    /// 是否存在于下载列表中
    /// </summary>
    /// <param name="downloading"></param>
    /// <returns></returns>
    private async Task<bool> IsExist(DownloadingItem downloading)
    {
        var isExist = DownloadingList.Contains(downloading);
        if (isExist)
        {
            return true;
        }
        else
        {
            // 先恢复为waiting状态，暂停状态下Remove会导致文件重新下载，原因暂不清楚
            var gid = downloading.Downloading.Gid;
            if (string.IsNullOrWhiteSpace(gid))
            {
                return false;
            }

            await AriaClient.UnpauseAsync(gid).ConfigureAwait(true);
            // 移除下载项
            var ariaRemove = await AriaClient.RemoveAsync(gid).ConfigureAwait(true);
            if (ariaRemove == null || ariaRemove.Result == gid)
            {
                // 从内存中删除下载项
                await AriaClient.RemoveDownloadResultAsync(gid).ConfigureAwait(true);
            }

            return false;
        }
    }

    /// <summary>
    /// 启动Aria服务器
    /// </summary>
    private async Task StartAriaServerAsync(CancellationToken cancellationToken)
    {
        var header = new List<string>
        {
            $"Cookie: {LoginHelper.GetLoginInfoCookiesString()}",
            $"Origin: https://www.bilibili.com",
            $"Referer: https://www.bilibili.com",
            $"User-Agent: {SettingsManager.Instance.GetUserAgent()}"
        };

        var config = new AriaConfig()
        {
            ListenPort = SettingsManager.Instance.GetAriaListenPort(),
            Token = "downkyi",
            LogLevel = SettingsManager.Instance.GetAriaLogLevel(),
            MaxConcurrentDownloads = SettingsManager.Instance.GetMaxCurrentDownloads(),
            MaxConnectionPerServer = SettingsManager.Instance.GetAriaMaxConnectionPerServer(),
            Split = SettingsManager.Instance.GetAriaSplit(),
            //MaxTries = 5,
            MinSplitSize = SettingsManager.Instance.GetAriaMinSplitSize(),
            MaxOverallDownloadLimit =
                SettingsManager.Instance.GetAriaMaxOverallDownloadLimit() * 1024L, // 输入的单位是KB/s，所以需要乘以1024
            MaxDownloadLimit = SettingsManager.Instance.GetAriaMaxDownloadLimit() * 1024L, // 输入的单位是KB/s，所以需要乘以1024
            ContinueDownload = true,
            FileAllocation = SettingsManager.Instance.GetAriaFileAllocation(),
            Headers = header
        };

        DownloadDiagnosticLogger.LogAriaServerConfig(Tag, config);

        var errorMessage = new StringBuilder();
        await AriaServer.StartServerAsync(config, output =>
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                errorMessage.AppendLine(output);
            }
        }).ConfigureAwait(true);

        // 显示错误信息
        var message = errorMessage.ToString();
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            var alertService = new AlertService(DialogService);
            await alertService.ShowMessage(SystemIcon.Instance().Error,
                $"Aria2 {DictionaryResource.GetString("Error")}",
                message,
                1).ConfigureAwait(true);
            return;
        }

        for (var i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var globOpt = await AriaClient.GetGlobalOptionAsync().ConfigureAwait(true);
            if (globOpt != null)
                break;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 关闭Aria服务器
    /// </summary>
    private async Task CloseAriaServer()
    {
        // 暂停所有下载
        try
        {
            var pauseTask = AriaClient.PauseAllAsync();
            await Task.WhenAny(pauseTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or InvalidOperationException
            or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(Tag, e);
        }
#if DEBUG
        Core.Utils.Debugging.Console.PrintLine("PauseAllAsync completed or timed out.");
#endif

        // 关闭服务器
        bool close = await AriaServer.CloseServerAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        if (!close)
        {
            close = await AriaServer.ForceCloseServerAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
#if DEBUG
        Core.Utils.Debugging.Console.PrintLine(close);
#endif
    }

    /// <summary>
    /// 采用Aria下载文件
    /// </summary>
    /// <param name="downloading"></param>
    /// <param name="urls"></param>
    /// <param name="path"></param>
    /// <param name="localFileName"></param>
    /// <returns></returns>
    private DownloadResult DownloadByAria(DownloadingItem downloading, List<string> urls, string path,
        string localFileName)
    {
        // path已斜杠结尾，去掉斜杠
        path = path.TrimEnd('/').TrimEnd('\\');

        //检查gid对应任务，如果已创建那么直接使用
        //但是代理设置会出现不能随时更新的问题

        if (downloading.Downloading.Gid != null)
        {
            var status = AriaClient.TellStatus(downloading.Downloading.Gid);
            if (status == null || status.Result == null)
                downloading.Downloading.Gid = null;
            else if (status.Result.Result == null && status.Result.Error != null)
            {
                if (status.Result.Error.Message.Contains("is not found"))
                {
                    downloading.Downloading.Gid = null;
                    PersistDownloadingState(downloading);
                }
            }
        }

        if (downloading.Downloading.Gid == null)
        {
            var option = new AriaSendOption
            {
                //HttpProxy = $"http://{Settings.GetAriaHttpProxy()}:{Settings.GetAriaHttpProxyListenPort()}",
                Dir = path,
                Out = localFileName,
                Continue = "true",
                AllowOverwrite = "true",
                AutoFileRenaming = "false",
                //Header = $"cookie: {LoginHelper.GetLoginInfoCookiesString()}\nreferer: https://www.bilibili.com",
                //UseHead = "true",
                UserAgent = SettingsManager.Instance.GetUserAgent(),
                Split = SettingsManager.Instance.GetAriaSplit().ToString(),
                MaxConnectionPerServer = SettingsManager.Instance.GetAriaMaxConnectionPerServer().ToString(),
                MinSplitSize = $"{SettingsManager.Instance.GetAriaMinSplitSize()}M",
            };

            // 如果设置了代理，则增加HttpProxy
            if (SettingsManager.Instance.GetIsAriaHttpProxy() == AllowStatus.Yes)
            {
                option.HttpProxy =
                    $"http://{SettingsManager.Instance.GetAriaHttpProxy()}:{SettingsManager.Instance.GetAriaHttpProxyListenPort()}";
            }

            // 添加一个下载
            var ariaAddUri = AriaClient.AddUriAsync(urls, option);
            if (ariaAddUri == null || ariaAddUri.Result == null || ariaAddUri.Result.Result == null)
            {
                return DownloadResult.FAILED;
            }

            // 保存gid
            var gid = ariaAddUri.Result.Result;
            downloading.Downloading.Gid = gid;
            PersistDownloadingState(downloading);
        }
        else
        {
            var ariaUnpause = AriaClient.UnpauseAsync(downloading.Downloading.Gid);
        }

        DownloadDiagnosticLogger.LogAriaTaskStart(Tag, downloading.Downloading.Gid, urls.Count);

        // 管理下载
        var ariaManager = new AriaManager();
        ariaManager.TellStatus += AriaTellStatus;
        ariaManager.DownloadFinish += AriaDownloadFinish;
        return ariaManager.GetDownloadStatusAsync(downloading.Downloading.Gid, new Action(() =>
        {
            CancellationToken?.ThrowIfCancellationRequested();
            switch (downloading.Downloading.DownloadStatus)
            {
                case DownloadStatus.Pause:
                    var ariaPause = AriaClient.PauseAsync(downloading.Downloading.Gid);
                    // 通知UI，并阻塞当前线程
                    Pause(downloading);
                    break;
                case DownloadStatus.Downloading:
                    break;
            }
        }), CancellationToken ?? System.Threading.CancellationToken.None).GetAwaiter().GetResult();
    }

    private void AriaTellStatus(long totalLength, long completedLength, long speed, string gid)
    {
        // 当前的下载视频
        DownloadingItem? video = null;
        try
        {
            video = DownloadingList.FirstOrDefault(it => it.Downloading.Gid == gid);
        }
        catch (InvalidOperationException e)
        {
            Core.Utils.Debugging.Console.PrintLine("AriaTellStatus()发生异常: {0}", e);
            LogManager.Error("AriaTellStatus()", e);
        }

        if (video == null)
        {
            return;
        }

        // 下载进度百分比
        float percent = 0;
        if (totalLength != 0)
        {
            percent = (float)completedLength / totalLength * 100;
        }

        // 根据进度判断本次是否需要更新UI
        if (Math.Abs(percent - video.Progress) < 0.01)
        {
            return;
        }

        // 下载进度
        video.Progress = percent;

        // 下载大小
        video.DownloadingFileSize = Format.FormatFileSize(completedLength) + "/" + Format.FormatFileSize(totalLength);

        // 下载速度
        video.SpeedDisplay = Format.FormatSpeedWithBandwidth(speed);
        DownloadDiagnosticLogger.LogSpeed(Tag, gid, completedLength, totalLength, speed);

        // 最大下载速度
        if (video.Downloading.MaxSpeed < speed)
        {
            video.Downloading.MaxSpeed = speed;
        }
    }

    private void AriaDownloadFinish(bool isSuccess, string? downloadPath, string gid, string? msg)
    {
        //throw new NotImplementedException();
    }
}
