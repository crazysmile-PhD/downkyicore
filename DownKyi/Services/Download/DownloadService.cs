using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Services.Download;

internal abstract class DownloadService : IDisposable
{
    private bool _disposed;
    protected string Tag { get; set; } = "DownloadService";

    // protected TaskbarIcon _notifyIcon;
    protected IDialogService? DialogService { get; }
    protected ImmutableObservableCollection<DownloadingItem> DownloadingList { get; }
    protected ImmutableObservableCollection<DownloadedItem> DownloadedList { get; }

    protected Task? WorkTask { get; set; }
    protected CancellationTokenSource? TokenSource { get; set; }
    protected CancellationToken? CancellationToken { get; set; }
    private readonly List<Task> _downloadingTasks = new();
    private readonly List<Task> _persistenceTasks = new();

    protected const int Retry = 5;
    protected const string NullMark = "<null>";

    private static DownloadStorageService DownloadStorageService =>
        (DownloadStorageService)App.Current.Container.Resolve(typeof(DownloadStorageService));

    protected void EnsureDownloadIsActive(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        CancellationToken?.ThrowIfCancellationRequested();
        if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause || !DownloadingList.Contains(downloading))
        {
            throw new OperationCanceledException("Task is paused or deleted");
        }
    }

    protected async Task PersistDownloadingStateAsync(DownloadingItem downloading)
    {
        try
        {
            await DownloadStorageService
                .UpdateDownloadingAsync(downloading, CancellationToken.GetValueOrDefault())
                .ConfigureAwait(true);
        }
        catch (SqliteException e)
        {
            LogManager.Debug(Tag, $"Persist downloading state failed: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            LogManager.Debug(Tag, $"Persist downloading state conflicted: {e.Message}");
        }
        catch (OperationCanceledException) when (CancellationToken?.IsCancellationRequested == true)
        {
        }
    }

    protected void PersistDownloadingState(DownloadingItem downloading)
    {
        var persistenceTask = PersistDownloadingStateAsync(downloading);
        lock (_persistenceTasks)
        {
            _persistenceTasks.RemoveAll(task => task.IsCompleted);
            _persistenceTasks.Add(persistenceTask);
        }
    }

    protected bool IsDownloadedMediaFileUsable(
        string? file,
        long expectedBytes = 0,
        long receivedBytes = 0,
        long totalBytesToReceive = 0)
    {
        var result = DownloadFileIntegrity.Check(file, expectedBytes, receivedBytes, totalBytesToReceive);
        if (!result.IsUsable)
        {
            LogManager.Info(Tag, result.Reason ?? "Downloaded media file is not usable.");
        }

        return result.IsUsable;
    }

    protected void DeleteInvalidDownloadedMediaFile(string? file)
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
                LogManager.Debug(Tag, $"Delete invalid media file failed: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                LogManager.Debug(Tag, $"Delete invalid media file was denied: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 初始化
    /// </summary>
    /// <param name="downloadingList"></param>
    /// <param name="downloadedList"></param>
    /// <param name="dialogService"></param>
    /// <returns></returns>
    protected DownloadService(ImmutableObservableCollection<DownloadingItem> downloadingList, ImmutableObservableCollection<DownloadedItem> downloadedList, IDialogService? dialogService)
    {
        DownloadingList = downloadingList;
        DownloadedList = downloadedList;
        DialogService = dialogService;
    }

    protected static PlayUrlDashVideo? BaseDownloadAudio(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingAudio");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        downloading.Progress = 0;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        // 如果没有Dash，返回null
        if (downloading.PlayUrl?.Dash == null)
        {
            return null;
        }

        // 如果audio列表没有内容，则返回null
        if (downloading.PlayUrl.Dash.Audio == null)
        {
            return null;
        }
        else if (downloading.PlayUrl.Dash.Audio.Count == 0)
        {
            return null;
        }

        // 根据音频id匹配
        PlayUrlDashVideo? downloadAudio = null;
        foreach (var audio in downloading.PlayUrl.Dash.Audio)
        {
            if (audio.Id == downloading.AudioCodec.Id)
            {
                downloadAudio = audio;
                break;
            }
        }

        if (downloading.AudioCodec.Id == 30250 &&
            downloading.PlayUrl.Dash.Dolby?.Audio is { Count: > 0 } dolbyAudio)
        {
            downloadAudio = dolbyAudio[0];
        }

        if (downloading.AudioCodec.Id == 30251 && downloading.PlayUrl.Dash.Flac?.Audio is { } flacAudio)
        {
            downloadAudio = flacAudio;
        }

        return downloadAudio;
    }

    protected static VideoPlayUrlBasic? BaseDownloadVideo(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingVideo");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        downloading.Progress = 0;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        if (downloading.PlayUrl?.Dash?.Video?.Count > 0)
        {
            foreach (var video in downloading.PlayUrl.Dash.Video)
            {
                var codecs = Constant.GetCodecIds().FirstOrDefault(t => t.Id == video.CodecId);
                if (video.Id == downloading.Resolution.Id && codecs?.Name == downloading.VideoCodecName)
                {
                    return new VideoPlayUrlBasic
                    {
                        BackupUrl = video.BackupUrl,
                        Codecs = video.Codecs,
                        Id = video.Id,
                        BaseUrl = video.BaseAddress
                    };
                }
            }
        }

        if (downloading?.PlayUrl?.Durl?.Count > 0)
        {
            return CreateDurlDownloadDescriptor(downloading.PlayUrl.Durl);
        }

        return null;
    }

    internal static VideoPlayUrlBasic? CreateDurlDownloadDescriptor(IEnumerable<PlayUrlDurl> durls)
    {
        ArgumentNullException.ThrowIfNull(durls);

        var durl = durls.OrderBy(item => item.Order).FirstOrDefault();
        if (durl == null)
        {
            return null;
        }

        return new VideoPlayUrlBasic
        {
            BackupUrl = durl.BackupUrl,
            BaseUrl = durl.SourceAddress,
            Codecs = "durl",
            Id = durl.Order,
            ExpectedSize = durl.Size
        };
    }

    protected async Task<string?> BaseDownloadCoverAsync(
        DownloadingItem downloading,
        string? coverUrl,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingCover");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        // 复制图片到指定位置
        try
        {
            if (string.IsNullOrWhiteSpace(coverUrl)) return null;
            WebClient.DownloadFile(coverUrl, fileName, cancellationToken: CancellationToken.GetValueOrDefault());

            // 记录本次下载的文件
            if (downloading.Downloading.DownloadFiles.TryAdd(coverUrl, fileName))
            {
                await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
            }
            return fileName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            Console.PrintLine($"{Tag}.DownloadCover()发生异常: {0}", e);
            LogManager.Error($"{Tag}.DownloadCover()", e);
        }
        catch (IOException e)
        {
            Console.PrintLine($"{Tag}.DownloadCover()发生IO异常: {0}", e);
            LogManager.Error($"{Tag}.DownloadCover()", e);
        }
        catch (UnauthorizedAccessException e)
        {
            Console.PrintLine($"{Tag}.DownloadCover()没有写入权限: {0}", e);
            LogManager.Error($"{Tag}.DownloadCover()", e);
        }

        return null;
    }

    protected async Task<string> BaseDownloadDanmakuAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingDanmaku");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        var title = $"{downloading.Name}";
        var assFile = $"{downloading.DownloadBase?.FilePath}.ass";

        // 记录本次下载的文件
        if (downloading.Downloading.DownloadFiles.TryAdd("danmaku", assFile))
        {
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }

        var screenWidth = SettingsManager.Instance.GetDanmakuScreenWidth();
        var screenHeight = SettingsManager.Instance.GetDanmakuScreenHeight();
        //if (SettingsManager.Instance.IsCustomDanmakuResolution() != AllowStatus.YES)
        //{
        //    if (downloadingEntity.Width > 0 && downloadingEntity.Height > 0)
        //    {
        //        screenWidth = downloadingEntity.Width;
        //        screenHeight = downloadingEntity.Height;
        //    }
        //}

        // 字幕配置
        var subtitleConfig = new Config
        {
            Title = title,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            FontName = SettingsManager.Instance.GetDanmakuFontName(),
            BaseFontSize = SettingsManager.Instance.GetDanmakuFontSize(),
            LineCount = SettingsManager.Instance.GetDanmakuLineCount(),
            LayoutAlgorithm =
                GetDanmakuLayoutAlgorithmValue(SettingsManager.Instance.GetDanmakuLayoutAlgorithm()), // async/sync
            TuneDuration = 0,
            DropOffset = 0,
            BottomMargin = 0,
            CustomOffset = 0
        };

        var bilibili = Core.Danmaku2Ass.BilibiliDanmakuConverter.Instance;
        bilibili.SetTopFilter(SettingsManager.Instance.GetDanmakuTopFilter() == AllowStatus.Yes);
        bilibili.SetBottomFilter(SettingsManager.Instance.GetDanmakuBottomFilter() == AllowStatus.Yes);
        bilibili.SetScrollFilter(SettingsManager.Instance.GetDanmakuScrollFilter() == AllowStatus.Yes);
        var downloadBase = downloading.DownloadBase ?? throw new InvalidOperationException("DownloadBase is required to download danmaku.");
        bilibili.Create(downloadBase.Avid, downloadBase.Cid, subtitleConfig, assFile, CancellationToken.GetValueOrDefault());

        return assFile;
    }


    protected async Task<IReadOnlyList<string>> BaseDownloadSubtitleAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingSubtitle");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        var srtFiles = new List<string>();

        var subRipTexts = VideoStreamApi.GetSubtitle(
            downloading.DownloadBase.Avid,
            downloading.DownloadBase.Bvid,
            downloading.DownloadBase.Cid,
            CancellationToken.GetValueOrDefault());
        foreach (var subRip in subRipTexts)
        {
            var srtFile = $"{downloading.DownloadBase.FilePath}_{subRip.LanDoc}.srt";
            try
            {
                await File.WriteAllTextAsync(
                    srtFile,
                    subRip.SrtString,
                    CancellationToken.GetValueOrDefault()).ConfigureAwait(true);

                // 记录本次下载的文件
                if (downloading.Downloading.DownloadFiles.TryAdd("subtitle", srtFile))
                {
                    await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
                }

                srtFiles.Add(srtFile);
            }
            catch (IOException e)
            {
                Console.PrintLine($"{Tag}.DownloadSubtitle()发生异常: {0}", e);
                LogManager.Error($"{Tag}.DownloadSubtitle()", e);
            }
            catch (UnauthorizedAccessException e)
            {
                Console.PrintLine($"{Tag}.DownloadSubtitle()没有写入权限: {0}", e);
                LogManager.Error($"{Tag}.DownloadSubtitle()", e);
            }
        }

        // subRipTexts中第一个复制为不带后缀的字幕,保证能自动匹配到字幕
        if (srtFiles.Count > 0)
        {
            var srtFile = $"{downloading.DownloadBase.FilePath}.srt";
            File.Copy(srtFiles[0], srtFile, true);
            srtFiles.Add(srtFile);
        }

        return srtFiles;
    }


    protected void GenerateNfoFile(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        var metadata = downloading.Metadata;
        if (metadata == null) return;

        var settings = new XmlWriterSettings { Indent = true };
        try
        {
            string filePath = $"{downloading.DownloadBase.FilePath}.nfo";
            using var writer = XmlWriter.Create(filePath, settings);
            WriteMovieMetadata(writer, metadata);
        }
        catch (IOException e)
        {
            LogManager.Error($"{Tag}.GenerateNfoFile()", e);
        }
        catch (UnauthorizedAccessException e)
        {
            LogManager.Error($"{Tag}.GenerateNfoFile()", e);
        }
        catch (XmlException e)
        {
            LogManager.Error($"{Tag}.GenerateNfoFile()", e);
        }
    }

    private static void WriteMovieMetadata(XmlWriter writer, MovieMetadata metadata)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("movie");

        writer.WriteElementString("title", metadata.Title);
        writer.WriteElementString("plot", metadata.Plot);
        writer.WriteElementString("year", metadata.Year);

        foreach (var genre in metadata.Genres)
        {
            writer.WriteElementString("genre", genre);
        }

        foreach (var tag in metadata.Tags)
        {
            writer.WriteElementString("tag", tag);
        }

        foreach (var actor in metadata.Actors)
        {
            writer.WriteStartElement("actor");
            writer.WriteElementString("name", actor.Name);
            writer.WriteElementString("role", actor.Role);
            writer.WriteEndElement();
        }

        if (metadata.BilibiliId != null)
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", metadata.BilibiliId.Type);
            writer.WriteString(metadata.BilibiliId.Value);
            writer.WriteEndElement();
        }

        writer.WriteElementString("premiered", metadata.Premiered);

        foreach (var rating in metadata.Ratings)
        {
            writer.WriteStartElement("rating");
            writer.WriteAttributeString("name", rating.Name);
            writer.WriteAttributeString("max", rating.Max.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("default", rating.IsDefault ? "true" : "false");
            writer.WriteString(rating.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }


    protected static string? BaseMixedFlow(DownloadingItem downloading, string? audioUid, string? videoUid)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("MixedFlow");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingVideo");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        //if (videoUid == nullMark)
        //{
        //    return null;
        //}

        var finalFile = $"{downloading.DownloadBase.FilePath}.mp4";
        if (videoUid == null)
        {
            finalFile = SettingsManager.Instance.GetIsTranscodingAacToMp3() == AllowStatus.Yes
                ? $"{downloading.DownloadBase.FilePath}.mp3"
                : downloading.AudioCodec.Id == 30251
                    ? $"{downloading.DownloadBase.FilePath}.flac"
                    : $"{downloading.DownloadBase.FilePath}.aac";
        }

        // 合并音视频
        FfmpegProcessor.Instance.MergeVideo(audioUid, videoUid, finalFile);

        // 获取文件大小
        if (File.Exists(finalFile))
        {
            var info = new FileInfo(finalFile);
            downloading.FileSize = Format.FormatFileSize(info.Length);
        }
        else
        {
            downloading.FileSize = Format.FormatFileSize(0);
        }

        return finalFile;
    }


    private static async Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        DownloadingItem downloading,
        IReadOnlyList<DurlDownloadResult> downloads,
        CancellationToken cancellationToken)
    {
        downloading.DownloadStatusTitle = DictionaryResource.GetString("ConcatVideos");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingVideo");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;

        var finalFile = $"{downloading.DownloadBase.FilePath}.mp4";
        var segments = downloads
            .OrderBy(download => download.Durl.Order)
            .Select(download => new FfmpegConcatSegment(
                download.Durl.Order,
                download.FilePath,
                TimeSpan.FromMilliseconds(download.Durl.Length)))
            .ToArray();
        var result = await FfmpegProcessor.Instance
            .ConcatDurlVideosAsync(segments, finalFile, cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        if (result.Succeeded && result.OutputPath != null)
        {
            var info = new FileInfo(result.OutputPath);
            downloading.FileSize = Format.FormatFileSize(info.Length);
        }
        else
        {
            downloading.FileSize = Format.FormatFileSize(0);
        }

        return result;
    }

    private sealed record DurlDownloadResult(PlayUrlDurl Durl, string FilePath);


    protected async Task BaseParseAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("Parsing");
        downloading.DownloadContent = string.Empty;
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        downloading.Progress = 0;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        if (downloading.PlayUrl != null && downloading.Downloading.DownloadStatus == DownloadStatus.NotStarted)
        {
            // 设置下载状态
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;

            return;
        }

        // 设置下载状态
        downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;

        PlayUrl? playUrl = null;
        switch (downloading.Downloading.PlayStreamType)
        {
            case PlayStreamType.Video:
                playUrl = downloading.PlayUrl ?? SettingsManager.Instance.VideoParseType switch
                {
                    0 => VideoStreamApi.GetVideoPlayUrl(downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                        cancellationToken: CancellationToken.GetValueOrDefault()),
                    1 => VideoStreamApi.GetVideoPlayUrlWebPage(downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                        downloading.DownloadBase.Page, CancellationToken.GetValueOrDefault()),
                    _ => throw new ArgumentException("Invalid video parse type. Valid values are: 0 (WebAPI) or 1 (WebPage).")
                };
                break;
            case PlayStreamType.Bangumi:
                playUrl = downloading.PlayUrl ?? VideoStreamApi.GetBangumiPlayUrl(downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid,
                    downloading.DownloadBase.Cid, cancellationToken: CancellationToken.GetValueOrDefault());
                break;
            case PlayStreamType.Cheese:
                playUrl = downloading.PlayUrl ?? VideoStreamApi.GetCheesePlayUrl(downloading.DownloadBase.Avid,
                    downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                    downloading.DownloadBase.EpisodeId, cancellationToken: CancellationToken.GetValueOrDefault());
                break;
            default:
                break;
        }

        if (playUrl == null)
        {
            await DownloadFailedAsync(downloading).ConfigureAwait(true);
            return;
        }

        downloading.PlayUrl = playUrl;
    }

    private readonly SemaphoreSlim _downloadSemaphore = new(SettingsManager.Instance
        .GetMaxCurrentDownloads());

    /// <summary>
    /// 执行任务
    /// </summary>
    protected async Task DoWork()
    {
        // 上次循环时正在下载的数量
        var lastDownloadingCount = 0;

        while (CancellationToken.HasValue &&
               !CancellationToken.Value.IsCancellationRequested)
        {
            try
            {
                _downloadingTasks.RemoveAll(task => task.IsCompleted);

                foreach (var downloading in DownloadingList)
                {
                    if (downloading.Downloading.DownloadStatus is not (DownloadStatus.NotStarted or DownloadStatus.WaitForDownload))
                        continue;

                    await _downloadSemaphore.WaitAsync(CancellationToken.Value).ConfigureAwait(true);
                    //这里需要立刻设置状态，否则如果SingleDownload没有及时执行，会重复创建任务
                    downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
                    await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
                    _downloadingTasks.Add(RunSingleDownloadAsync(downloading));
                }
            }
            catch (ObjectDisposedException e)
            {
                Console.PrintLine($"{Tag}.DoWork()资源已释放: {0}", e);
                LogManager.Error($"{Tag}.DoWork() ObjectDisposedException", e);
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine($"{Tag}.DoWork()发生InvalidOperationException异常: {0}", e);
                LogManager.Error($"{Tag}.DoWork() InvalidOperationException", e);
            }

            // 判断是否该结束线程，若为true，跳出while循环
            if (CancellationToken?.IsCancellationRequested == true)
            {
                Console.PrintLine($"{Tag}.DoWork() 下载服务结束，跳出while循环");
                LogManager.Debug($"{Tag}.DoWork()", "下载服务结束");
                break;
            }

            // 判断下载列表中的视频是否全部下载完成
            if (lastDownloadingCount > 0 && DownloadingList.Count == 0 && DownloadedList.Count > 0)
            {
                AfterDownload();
            }

            lastDownloadingCount = DownloadingList.Count;

            // 降低CPU占用
            await Task.Delay(500).ConfigureAwait(true);
        }

        await Task.WhenAny(Task.WhenAll(_downloadingTasks), Task.Delay(30000)).ConfigureAwait(true);
        foreach (var tsk in _downloadingTasks.FindAll(task => !task.IsCompleted))
        {
            Console.PrintLine($"{Tag}.DoWork() 任务结束超时");
            LogManager.Debug($"{Tag}.DoWork()", "任务结束超时");
        }
    }

    private static string GetDanmakuLayoutAlgorithmValue(DanmakuLayoutAlgorithm algorithm)
    {
        return algorithm switch
        {
            DanmakuLayoutAlgorithm.None => "none",
            DanmakuLayoutAlgorithm.Async => "async",
            DanmakuLayoutAlgorithm.Sync => "sync",
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported danmaku layout algorithm.")
        };
    }

    private async Task RunSingleDownloadAsync(DownloadingItem downloading)
    {
        try
        {
            await SingleDownload(downloading).ConfigureAwait(true);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// 下载一个视频
    /// </summary>
    /// <param name="downloading"></param>
    /// <returns></returns>
    private async Task SingleDownload(DownloadingItem downloading)
    {
        // 路径
        downloading.DownloadBase.FilePath = downloading.DownloadBase.FilePath.Replace("\\", "/", StringComparison.Ordinal);
        var temp = downloading.DownloadBase.FilePath.Split('/');
        //string path = downloading.DownloadBase.FilePath.Replace(temp[temp.Length - 1], "");
        var path = downloading.DownloadBase.FilePath.TrimEnd(temp[temp.Length - 1].ToCharArray());

        // 路径不存在则创建
        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (IOException e)
            {
                Console.PrintLine(Tag, e.ToString());
                LogManager.Debug(Tag, e.Message);

                var alertService = new AlertService(DialogService);
                await alertService.ShowError($"{path}{DictionaryResource.GetString("DirectoryError")}").ConfigureAwait(true);

                return;
            }
            catch (UnauthorizedAccessException e)
            {
                Console.PrintLine(Tag, e.ToString());
                LogManager.Debug(Tag, e.Message);

                var alertService = new AlertService(DialogService);
                await alertService.ShowError($"{path}{DictionaryResource.GetString("DirectoryError")}").ConfigureAwait(true);

                return;
            }
        }

        try
        {
            await Task.Run(async () =>
            {
                // 初始化
                downloading.DownloadStatusTitle = string.Empty;
                downloading.DownloadContent = string.Empty;
                //downloading.Downloading.DownloadFiles.Clear();

                // 解析并依次下载音频、视频、弹幕、字幕、封面等内容
                await ParseAsync(downloading).ConfigureAwait(true);

                // 暂停
                Pause(downloading);

                var isMediaSuccess = true;

                if (downloading.PlayUrl.Dash != null)
                {
                    string? audioUid = null;

                    string? videoUid = null;
                    // 如果需要下载音频
                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"])
                    {
                        for (var i = 0; i < Retry; i++)
                        {
                            audioUid = DownloadAudio(downloading);
                            if (audioUid != null && audioUid != NullMark)
                            {
                                break;
                            }
                        }
                    }

                    if (audioUid == NullMark)
                    {
                        await DownloadFailedAsync(downloading).ConfigureAwait(true);
                        return;
                    }

                    Pause(downloading);


                    // 如果需要下载视频
                    if (downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        //videoUid = DownloadVideo(downloading);
                        for (var i = 0; i < Retry; i++)
                        {
                            videoUid = DownloadVideo(downloading);
                            if (videoUid != null && videoUid != NullMark)
                            {
                                break;
                            }
                        }
                    }

                    if (videoUid == NullMark)
                    {
                        await DownloadFailedAsync(downloading).ConfigureAwait(true);
                        return;
                    }

                    Pause(downloading);

                    // 混流
                    var outputMedia = string.Empty;
                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] ||
                        downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        outputMedia = MixedFlow(downloading, audioUid, videoUid);
                    }

                    // 检测音频、视频是否下载成功

                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] ||
                        downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        // 只有下载音频不下载视频时才输出aac
                        // 只要下载视频就输出mp4
                        // 成功
                        isMediaSuccess = File.Exists(outputMedia);
                    }
                }
                else if (downloading.PlayUrl.Durl.Count > 0)
                {
                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] ||
                        downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        var durls = downloading.PlayUrl.Durl
                            .OrderBy(durl => durl.Order)
                            .ToList();
                        var downloadStatus = durls
                            .Select(durl => new DurlDownloadResult(durl, NullMark))
                            .ToArray();
                        var originalDurls = downloading.PlayUrl.Durl;
                        try
                        {
                            for (var retryCount = 0; retryCount < Retry; retryCount++)
                            {
                                for (var index = 0; index < downloadStatus.Length; index++)
                                {
                                    if (downloadStatus[index].FilePath != NullMark)
                                    {
                                        continue;
                                    }

                                    downloading.PlayUrl.Durl = new[] { downloadStatus[index].Durl };
                                    var result = DownloadVideo(downloading);
                                    downloadStatus[index] = downloadStatus[index] with
                                    {
                                        FilePath = result ?? NullMark
                                    };
                                }

                                if (downloadStatus.All(download => download.FilePath != NullMark))
                                {
                                    break;
                                }

                                await Task.Delay(
                                        TimeSpan.FromSeconds(1),
                                        CancellationToken.GetValueOrDefault())
                                    .ConfigureAwait(true);
                            }
                        }
                        finally
                        {
                            downloading.PlayUrl.Durl = originalDurls;
                        }

                        if (downloadStatus.Any(download => download.FilePath == NullMark))
                        {
                            await DownloadFailedAsync(downloading).ConfigureAwait(true);
                            return;
                        }

                        Pause(downloading);

                        if (durls.Count > 1)
                        {
                            var concatResult = await ConcatDurlVideosAsync(
                                    downloading,
                                    downloadStatus,
                                    CancellationToken.GetValueOrDefault())
                                .ConfigureAwait(true);
                            isMediaSuccess = concatResult.Succeeded;
                        }
                        else
                        {
                            var outputMedia = MixedFlow(downloading, null, downloadStatus[0].FilePath);
                            isMediaSuccess = File.Exists(outputMedia);
                        }
                    }

                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] &&
                        !downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        //音频分离？
                    }

                    Pause(downloading);
                }
                else
                {
                    await DownloadFailedAsync(downloading).ConfigureAwait(true);
                    return;
                }


                //nfo
                if (SettingsManager.Instance
                    .GetVideoContent().GenerateMovieMetadata)
                {
                    GenerateNfoFile(downloading);
                }

                string? outputDanmaku = null;
                // 如果需要下载弹幕
                if (downloading.DownloadBase.NeedDownloadContent["downloadDanmaku"])
                {
                    outputDanmaku = await DownloadDanmakuAsync(downloading).ConfigureAwait(true);
                }

                // 暂停
                Pause(downloading);

                IReadOnlyList<string>? outputSubtitles = null;
                // 如果需要下载字幕
                if (downloading.DownloadBase.NeedDownloadContent["downloadSubtitle"])
                {
                    outputSubtitles = await DownloadSubtitleAsync(downloading).ConfigureAwait(true);
                }

                // 暂停
                Pause(downloading);

                string? outputCover = null;
                string? outputPageCover = null;
                // 如果需要下载封面
                if (downloading.DownloadBase.NeedDownloadContent["downloadCover"])
                {
                    // page的封面
                    var pageCoverFileName = $"{downloading.DownloadBase.FilePath}.{GetImageExtension(downloading.DownloadBase.PageCoverUrl)}";
                    outputPageCover = await DownloadCoverAsync(
                        downloading,
                        downloading.DownloadBase.PageCoverUrl,
                        pageCoverFileName).ConfigureAwait(true);


                    var coverFileName = $"{downloading.DownloadBase.FilePath}.Cover.{GetImageExtension(downloading.DownloadBase.CoverUrl)}";
                    // 封面
                    //outputCover = DownloadCover(downloading, downloading.DownloadBase.CoverUrl, $"{path}/Cover.{GetImageExtension(downloading.DownloadBase.CoverUrl)}");
                    outputCover = await DownloadCoverAsync(
                        downloading,
                        downloading.DownloadBase.CoverUrl,
                        coverFileName).ConfigureAwait(true);
                }

                // 暂停
                Pause(downloading);

                // 这里本来只有IsExist，没有pause，不知道怎么处理
                // 是否存在
                //isExist = IsExist(downloading);
                //if (!isExist.Result)
                //{
                //    return;
                //}

                // 检测弹幕是否下载成功
                var isDanmakuSuccess = true;
                if (downloading.DownloadBase.NeedDownloadContent["downloadDanmaku"])
                {
                    // 成功
                    isDanmakuSuccess = File.Exists(outputDanmaku);
                }

                // 检测字幕是否下载成功
                var isSubtitleSuccess = true;
                if (downloading.DownloadBase.NeedDownloadContent["downloadSubtitle"])
                {
                    if (outputSubtitles == null)
                    {
                        // 为null时表示不存在字幕
                    }
                    else
                    {
                        foreach (var subtitle in outputSubtitles)
                        {
                            if (!File.Exists(subtitle))
                            {
                                // 如果有一个不存在则失败
                                isSubtitleSuccess = false;
                            }
                        }
                    }
                }

                // 检测封面是否下载成功
                var isCover = true;
                if (downloading.DownloadBase.NeedDownloadContent["downloadCover"])
                {
                    if (File.Exists(outputCover) || File.Exists(outputPageCover))
                    {
                        // 成功
                        isCover = true;
                    }
                    else
                    {
                        isCover = false;
                    }
                }

                if (!isMediaSuccess || !isDanmakuSuccess || !isSubtitleSuccess || !isCover)
                {
                    await DownloadFailedAsync(downloading).ConfigureAwait(true);
                    return;
                }

                // 下载完成后处理
                var downloaded = new Downloaded
                {
                    MaxSpeedDisplay = Format.FormatSpeedWithBandwidth(downloading.Downloading.MaxSpeed),
                };
                // 设置完成时间
                downloaded.SetFinishedTimestamp(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds());

                var downloadedItem = new DownloadedItem
                {
                    DownloadBase = downloading.DownloadBase,
                    Downloaded = downloaded
                };

                await DownloadStorageService
                    .AddDownloadedAsync(downloadedItem, CancellationToken.GetValueOrDefault())
                    .ConfigureAwait(true);
                App.PropertyChangeAsync(() =>
                {
                    // 加入到下载完成list中，并从下载中list去除
                    DownloadedList.Add(downloadedItem);
                    DownloadingList.Remove(downloading);

                    // 下载完成列表排序
                    var finishedSort = SettingsManager.Instance.GetDownloadFinishedSort();
                    App.SortDownloadedList(finishedSort);
                });
                // _notifyIcon.ShowBalloonTip(DictionaryResource.GetString("DownloadSuccess"), $"{downloadedItem.DownloadBase.Name}", BalloonIcon.Info);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException e)
        {
            Console.PrintLine(Tag, e.ToString());
            LogManager.Debug(Tag, e.Message);
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 下载失败后的处理
    /// </summary>
    /// <param name="downloading"></param>
    protected async Task DownloadFailedAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        downloading.DownloadStatusTitle = DictionaryResource.GetString("DownloadFailed");
        downloading.DownloadContent = string.Empty;
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;
        downloading.Progress = 0;

        downloading.Downloading.DownloadStatus = DownloadStatus.DownloadFailed;
        downloading.StartOrPause = ButtonIcon.Instance().Retry;
        downloading.StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");
        await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
    }

    /// <summary>
    /// 获取图片的扩展名
    /// </summary>
    /// <param name="coverUrl"></param>
    /// <returns></returns>
    private static string GetImageExtension(string? coverUrl)
    {
        if (coverUrl == null)
        {
            return string.Empty;
        }

        // 图片的扩展名
        var temp = coverUrl.Split('.');
        var fileExtension = temp[^1];
        return fileExtension;
    }

    /// <summary>
    /// 下载完成后的操作
    /// </summary>
    private static void AfterDownload()
    {
        var operation = SettingsManager.Instance.GetAfterDownloadOperation();
        switch (operation)
        {
            case AfterDownloadOperation.None:
                // 没有操作
                break;
            case AfterDownloadOperation.OpenFolder:
                // 打开文件夹
                break;
            case AfterDownloadOperation.CloseApp:
                // 关闭程序
                App.PropertyChangeAsync(() =>
                {
                    // System.Windows.Application.Current.Shutdown();
                });
                break;
            case AfterDownloadOperation.CloseSystem:
                // 关机
                // Process.Start("shutdown.exe", "-s");
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 停止基本下载服务(转换await和Task.Wait两种调用形式)
    /// </summary>
    protected async Task BaseEndTask()
    {
        // 结束任务
        if (TokenSource != null)
        {
            await TokenSource.CancelAsync().ConfigureAwait(true);
        }

        if (WorkTask != null) await WorkTask.ConfigureAwait(true);

        Task[] persistenceTasks;
        lock (_persistenceTasks)
        {
            persistenceTasks = [.. _persistenceTasks];
        }

        await Task.WhenAll(persistenceTasks).ConfigureAwait(true);

        //先简单等待一下

        // 下载数据存储服务
        var downloadStorageService = (DownloadStorageService)App.Current.Container.Resolve(typeof(DownloadStorageService));
        // 保存数据
        foreach (var item in DownloadingList)
        {
            switch (item.Downloading.DownloadStatus)
            {
                case DownloadStatus.NotStarted:
                case DownloadStatus.WaitForDownload:
                case DownloadStatus.PauseStarted:
                case DownloadStatus.Pause:
                    break;
                case DownloadStatus.Downloading:
                    // TODO 添加设置让用户选择重启后是否自动开始下载
                    item.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                    //item.Downloading.DownloadStatus = DownloadStatus.PAUSE;
                    break;
                case DownloadStatus.DownloadSucceed:
                case DownloadStatus.DownloadFailed:
                default:
                    break;
            }

            item.SpeedDisplay = string.Empty;

            await downloadStorageService.UpdateDownloadingAsync(item).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        TokenSource?.Cancel();
        TokenSource?.Dispose();
        TokenSource = null;
        _downloadSemaphore.Dispose();
    }

    /// <summary>
    /// 启动基本下载服务
    /// </summary>
    protected void BaseStart()
    {
        TokenSource = new CancellationTokenSource();
        CancellationToken = TokenSource.Token;
        // _notifyIcon = new TaskbarIcon();
        // _notifyIcon.IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/favicon.ico"));

        WorkTask = Task.Run(DoWork);
    }

    #region 抽象接口函数

    public abstract Task ParseAsync(DownloadingItem downloading);
    public abstract string? DownloadAudio(DownloadingItem downloading);
    public abstract string? DownloadVideo(DownloadingItem downloading);
    public abstract Task<string> DownloadDanmakuAsync(DownloadingItem downloading);
    public abstract Task<IReadOnlyList<string>> DownloadSubtitleAsync(DownloadingItem downloading);
    public abstract Task<string?> DownloadCoverAsync(
        DownloadingItem downloading,
        string? coverUrl,
        string fileName);
    public abstract string? MixedFlow(DownloadingItem downloading, string? audioUid, string? videoUid);

    protected abstract void Pause(DownloadingItem downloading);
    #endregion
}
