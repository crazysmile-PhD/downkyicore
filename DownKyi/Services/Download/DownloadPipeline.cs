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
using DownKyi.Application.Desktop;
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
using DownKyi.Platform;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPipeline : IDisposable
{
    private bool _disposed;
    private string Tag => _transferBackend.Name;

    // protected TaskbarIcon _notifyIcon;
    private IUserNotificationService NotificationService { get; }
    private DownloadListState DownloadLists { get; }
    private ImmutableObservableCollection<DownloadingItem> DownloadingList { get; }
    private ImmutableObservableCollection<DownloadedItem> DownloadedList { get; }
    private DownloadTaskProjectionStore ProjectionStore { get; }
    private IUiDispatcher UiDispatcher { get; }
    private DownloadDiagnosticLogger DiagnosticLogger { get; }
    private FfmpegProcessor FfmpegProcessor { get; }
    private ApplicationSettings Settings => SettingsStore.Current;
    private ISettingsStore SettingsStore { get; }
    private ILogger Logger { get; }

    private CancellationToken? CancellationToken { get; set; }
    private readonly ITransferBackend _transferBackend;

    private const int Retry = 5;
    private const string NullMark = "<null>";

    private void EnsureDownloadIsActive(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        CancellationToken?.ThrowIfCancellationRequested();
        if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause || !DownloadingList.Contains(downloading))
        {
            throw new OperationCanceledException("Task is paused or deleted");
        }
    }

    internal async Task PersistDownloadingStateAsync(DownloadingItem downloading)
    {
        try
        {
            await ProjectionStore
                .UpdateDownloadingAsync(downloading, CancellationToken.GetValueOrDefault())
                .ConfigureAwait(true);
        }
        catch (SqliteException e)
        {
            Logger.LogDebugMessage($"Persist downloading state failed: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            Logger.LogDebugMessage($"Persist downloading state conflicted: {e.Message}");
        }
        catch (OperationCanceledException) when (CancellationToken?.IsCancellationRequested == true)
        {
            return;
        }
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
            Logger.LogInformationMessage(result.Reason ?? "Downloaded media file is not usable.");
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
                Logger.LogDebugMessage($"Delete invalid media file failed: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Logger.LogDebugMessage($"Delete invalid media file was denied: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 初始化
    /// </summary>
    /// <param name="downloadLists"></param>
    /// <param name="projectionStore"></param>
    /// <param name="dialogService"></param>
    /// <returns></returns>
    public DownloadPipeline(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IUserNotificationService notificationService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        ITransferBackend transferBackend,
        ILogger logger)
    {
        DownloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        ProjectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        DownloadingList = downloadLists.Downloading;
        DownloadedList = downloadLists.Downloaded;
        NotificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        UiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        DiagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        FfmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _transferBackend = transferBackend ?? throw new ArgumentNullException(nameof(transferBackend));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static PlayUrlDashVideo? BaseDownloadAudio(DownloadingItem downloading)
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

    private static VideoPlayUrlBasic? BaseDownloadVideo(DownloadingItem downloading)
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

    public Task<string?> DownloadAudioAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        return DownloadMediaAsync(downloading, BaseDownloadAudio(downloading));
    }

    public Task<string?> DownloadVideoAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        var descriptor = BaseDownloadVideo(downloading);
        return descriptor == null
            ? Task.FromResult<string?>(null)
            : DownloadMediaAsync(downloading, new PlayUrlDashVideo
            {
                Id = descriptor.Id,
                Codecs = descriptor.Codecs,
                BaseAddress = descriptor.BaseUrl,
                BackupUrl = descriptor.BackupUrl,
                ExpectedSize = descriptor.ExpectedSize
            });
    }

    private async Task<string?> DownloadMediaAsync(
        DownloadingItem downloading,
        PlayUrlDashVideo? media)
    {
        if (media == null)
        {
            return null;
        }

        EnsureDownloadIsActive(downloading);
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(media.BaseAddress))
        {
            urls.Add(media.BaseAddress);
        }

        urls.AddRange(media.BackupUrl.Where(url => !string.IsNullOrWhiteSpace(url)));
        if (urls.Count == 0)
        {
            return NullMark;
        }

        var normalizedBasePath = downloading.DownloadBase.FilePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetDirectoryName(normalizedBasePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return NullMark;
        }

        var fileName = Guid.NewGuid().ToString("N");
        var key = VideoPlayUrlBasic.CreateDownloadKey(media.Id, media.Codecs);
        downloading.Downloading.DownloadedFiles ??= [];
        if (downloading.Downloading.DownloadFiles.TryGetValue(key, out var existingFileName))
        {
            fileName = existingFileName;
            var cachedFile = Path.Combine(path, fileName);
            if (downloading.Downloading.DownloadedFiles.Contains(key) &&
                IsDownloadedMediaFileUsable(cachedFile, media.ExpectedSize))
            {
                return cachedFile;
            }

            if (downloading.Downloading.DownloadedFiles.Remove(key))
            {
                DeleteInvalidDownloadedMediaFile(cachedFile);
                await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
            }
        }
        else if (downloading.Downloading.DownloadFiles.TryAdd(key, fileName))
        {
            downloading.Downloading.Gid = null;
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }

        NormalizeTransferSchemes(urls, Settings.Network.UseSsl == AllowStatus.Yes);
        var targetFile = Path.Combine(path, fileName);
        var outcome = await TransferAsync(
            downloading,
            urls,
            path,
            fileName,
            media.ExpectedSize).ConfigureAwait(true);
        if (outcome == DownloadTransferOutcome.Succeeded &&
            IsDownloadedMediaFileUsable(targetFile, media.ExpectedSize))
        {
            if (!downloading.Downloading.DownloadedFiles.Contains(key))
            {
                downloading.Downloading.DownloadedFiles.Add(key);
            }

            downloading.Downloading.Gid = null;
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
            return targetFile;
        }

        if (outcome != DownloadTransferOutcome.Paused)
        {
            downloading.Downloading.Gid = null;
            DeleteInvalidDownloadedMediaFile(targetFile);
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }

        return NullMark;
    }

    private static void NormalizeTransferSchemes(List<string> urls, bool useSsl)
    {
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            if (useSsl && url.StartsWith("http://", StringComparison.Ordinal))
            {
                urls[index] = "https://" + url["http://".Length..];
            }
            else if (!useSsl && url.StartsWith("https://", StringComparison.Ordinal))
            {
                urls[index] = "http://" + url["https://".Length..];
            }
        }
    }

    private async Task<string?> BaseDownloadCoverAsync(
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
            Logger.LogErrorMessage("Cover download failed.", e);
        }
        catch (IOException e)
        {
            Logger.LogErrorMessage("Cover download timed out.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            Logger.LogErrorMessage("Cover download was denied.", e);
        }

        return null;
    }

    private async Task<string> BaseDownloadDanmakuAsync(DownloadingItem downloading)
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

        var danmakuSettings = Settings.Danmaku;
        var screenWidth = danmakuSettings.ScreenWidth;
        var screenHeight = danmakuSettings.ScreenHeight;
        //if (Settings.IsCustomDanmakuResolution() != AllowStatus.YES)
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
            FontName = danmakuSettings.FontName,
            BaseFontSize = danmakuSettings.FontSize,
            LineCount = danmakuSettings.LineCount,
            LayoutAlgorithm =
                GetDanmakuLayoutAlgorithmValue(danmakuSettings.LayoutAlgorithm), // async/sync
            TuneDuration = 0,
            DropOffset = 0,
            BottomMargin = 0,
            CustomOffset = 0
        };

        var bilibili = Core.Danmaku2Ass.BilibiliDanmakuConverter.Instance;
        bilibili.SetTopFilter(danmakuSettings.TopFilter == AllowStatus.Yes);
        bilibili.SetBottomFilter(danmakuSettings.BottomFilter == AllowStatus.Yes);
        bilibili.SetScrollFilter(danmakuSettings.ScrollFilter == AllowStatus.Yes);
        var downloadBase = downloading.DownloadBase ?? throw new InvalidOperationException("DownloadBase is required to download danmaku.");
        bilibili.Create(downloadBase.Avid, downloadBase.Cid, subtitleConfig, assFile, CancellationToken.GetValueOrDefault());

        return assFile;
    }


    private async Task<IReadOnlyList<string>> BaseDownloadSubtitleAsync(DownloadingItem downloading)
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
            SettingsStore,
            downloading.DownloadBase.Avid,
            downloading.DownloadBase.Bvid,
            downloading.DownloadBase.Cid,
            e => Logger.LogErrorMessage("Subtitle response parsing failed.", e),
            CancellationToken.GetValueOrDefault());
        if (subRipTexts.Count == 0)
        {
            Logger.LogWarningMessage("No usable subtitles were returned for the download task.");
        }

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
                Logger.LogErrorMessage("Subtitle download failed.", e);
            }
            catch (UnauthorizedAccessException e)
            {
                Logger.LogErrorMessage("Subtitle download was denied.", e);
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


    private void GenerateNfoFile(DownloadingItem downloading)
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
            Logger.LogErrorMessage("NFO generation failed.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            Logger.LogErrorMessage("NFO generation was denied.", e);
        }
        catch (XmlException e)
        {
            Logger.LogErrorMessage("NFO generation produced invalid XML.", e);
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


    private async Task<string?> BaseMixedFlowAsync(
        DownloadingItem downloading,
        string? audioUid,
        string? videoUid,
        CancellationToken cancellationToken)
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
            finalFile = Settings.Video.IsTranscodingAacToMp3 == AllowStatus.Yes
                ? $"{downloading.DownloadBase.FilePath}.mp3"
                : downloading.AudioCodec.Id == 30251
                    ? $"{downloading.DownloadBase.FilePath}.flac"
                    : $"{downloading.DownloadBase.FilePath}.aac";
        }

        // 合并音视频
        var succeeded = await FfmpegProcessor
            .MergeVideoAsync(audioUid, videoUid, finalFile, cancellationToken)
            .ConfigureAwait(true);
        if (!succeeded)
        {
            downloading.FileSize = Format.FormatFileSize(0);
            return null;
        }

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


    private async Task<FfmpegOperationResult> ConcatDurlVideosAsync(
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
        var result = await FfmpegProcessor
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


    private async Task BaseParseAsync(DownloadingItem downloading)
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
                playUrl = downloading.PlayUrl ?? Settings.Video.VideoParseType switch
                {
                    0 => VideoStreamApi.GetVideoPlayUrl(SettingsStore, downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                        cancellationToken: CancellationToken.GetValueOrDefault()),
                    1 => VideoStreamApi.GetVideoPlayUrlWebPage(SettingsStore, downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
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
            await MarkFailedAsync(downloading).ConfigureAwait(true);
            return;
        }

        downloading.PlayUrl = playUrl;
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

    /// <summary>
    /// 下载一个视频
    /// </summary>
    /// <param name="downloading"></param>
    /// <returns></returns>
    internal async Task ExecuteAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
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
                Logger.LogDebugMessage(e.Message);

                NotificationService.Show($"{path}{DictionaryResource.GetString("DirectoryError")}");

                return;
            }
            catch (UnauthorizedAccessException e)
            {
                Logger.LogDebugMessage(e.Message);

                NotificationService.Show($"{path}{DictionaryResource.GetString("DirectoryError")}");

                return;
            }
        }

        try
        {
            {
                // 初始化
                downloading.DownloadStatusTitle = string.Empty;
                downloading.DownloadContent = string.Empty;
                //downloading.Downloading.DownloadFiles.Clear();

                // 解析并依次下载音频、视频、弹幕、字幕、封面等内容
                await ParseAsync(downloading).ConfigureAwait(true);

                // 暂停
                EnsureDownloadIsActive(downloading);

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
                            audioUid = await DownloadAudioAsync(downloading).ConfigureAwait(true);
                            if (audioUid != null && audioUid != NullMark)
                            {
                                break;
                            }
                        }
                    }

                    if (audioUid == NullMark)
                    {
                        await MarkFailedAsync(downloading).ConfigureAwait(true);
                        return;
                    }

                    EnsureDownloadIsActive(downloading);


                    // 如果需要下载视频
                    if (downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        //videoUid = DownloadVideo(downloading);
                        for (var i = 0; i < Retry; i++)
                        {
                            videoUid = await DownloadVideoAsync(downloading).ConfigureAwait(true);
                            if (videoUid != null && videoUid != NullMark)
                            {
                                break;
                            }
                        }
                    }

                    if (videoUid == NullMark)
                    {
                        await MarkFailedAsync(downloading).ConfigureAwait(true);
                        return;
                    }

                    EnsureDownloadIsActive(downloading);

                    // 混流
                    var outputMedia = string.Empty;
                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] ||
                        downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        outputMedia = await MixedFlowAsync(downloading, audioUid, videoUid).ConfigureAwait(true);
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
                                    var result = await DownloadVideoAsync(downloading).ConfigureAwait(true);
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
                                        cancellationToken)
                                    .ConfigureAwait(true);
                            }
                        }
                        finally
                        {
                            downloading.PlayUrl.Durl = originalDurls;
                        }

                        if (downloadStatus.Any(download => download.FilePath == NullMark))
                        {
                            await MarkFailedAsync(downloading).ConfigureAwait(true);
                            return;
                        }

                        EnsureDownloadIsActive(downloading);

                        if (durls.Count > 1)
                        {
                            var concatResult = await ConcatDurlVideosAsync(
                                    downloading,
                                    downloadStatus,
                                    cancellationToken)
                                .ConfigureAwait(true);
                            isMediaSuccess = concatResult.Succeeded;
                        }
                        else
                        {
                            var outputMedia = await MixedFlowAsync(
                                downloading,
                                null,
                                downloadStatus[0].FilePath).ConfigureAwait(true);
                            isMediaSuccess = File.Exists(outputMedia);
                        }
                    }

                    if (downloading.DownloadBase.NeedDownloadContent["downloadAudio"] &&
                        !downloading.DownloadBase.NeedDownloadContent["downloadVideo"])
                    {
                        //音频分离？
                    }

                    EnsureDownloadIsActive(downloading);
                }
                else
                {
                    await MarkFailedAsync(downloading).ConfigureAwait(true);
                    return;
                }


                //nfo
                if (Settings.Video.Content.GenerateMovieMetadata)
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
                EnsureDownloadIsActive(downloading);

                IReadOnlyList<string>? outputSubtitles = null;
                // 如果需要下载字幕
                if (downloading.DownloadBase.NeedDownloadContent["downloadSubtitle"])
                {
                    outputSubtitles = await DownloadSubtitleAsync(downloading).ConfigureAwait(true);
                }

                // 暂停
                EnsureDownloadIsActive(downloading);

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
                EnsureDownloadIsActive(downloading);

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
                    await MarkFailedAsync(downloading).ConfigureAwait(true);
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

                await ProjectionStore
                    .AddDownloadedAsync(downloadedItem, cancellationToken)
                    .ConfigureAwait(true);
                await UiDispatcher.InvokeAsync(() =>
                {
                    // 加入到下载完成list中，并从下载中list去除
                    DownloadedList.Add(downloadedItem);
                    DownloadingList.Remove(downloading);

                    // 下载完成列表排序
                    var finishedSort = Settings.Basic.DownloadFinishedSort;
                    DownloadLists.SortDownloaded(finishedSort);
                }).ConfigureAwait(true);
                // _notifyIcon.ShowBalloonTip(DictionaryResource.GetString("DownloadSuccess"), $"{downloadedItem.DownloadBase.Name}", BalloonIcon.Info);
            }
        }
        catch (OperationCanceledException e)
        {
            Logger.LogDebugMessage(e.Message);
            await PersistDownloadingStateAsync(downloading).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 下载失败后的处理
    /// </summary>
    /// <param name="downloading"></param>
    internal async Task MarkFailedAsync(DownloadingItem downloading)
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

    internal async Task PersistShutdownStateAsync()
    {
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
                    item.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                    break;
                case DownloadStatus.DownloadSucceed:
                case DownloadStatus.DownloadFailed:
                default:
                    break;
            }

            item.SpeedDisplay = string.Empty;

            await ProjectionStore.UpdateDownloadingAsync(item).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
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

        _transferBackend.Dispose();
    }

    private Task ParseAsync(DownloadingItem downloading) => BaseParseAsync(downloading);
    private Task<string> DownloadDanmakuAsync(DownloadingItem downloading) => BaseDownloadDanmakuAsync(downloading);
    private Task<IReadOnlyList<string>> DownloadSubtitleAsync(DownloadingItem downloading) =>
        BaseDownloadSubtitleAsync(downloading);
    private Task<string?> DownloadCoverAsync(
        DownloadingItem downloading,
        string? coverUrl,
        string fileName) => BaseDownloadCoverAsync(downloading, coverUrl, fileName);
    private async Task<string?> MixedFlowAsync(DownloadingItem downloading, string? audioUid, string? videoUid)
    {
        EnsureDownloadIsActive(downloading);
        return await BaseMixedFlowAsync(
            downloading,
            audioUid,
            videoUid,
            CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
    }

    private Task<DownloadTransferOutcome> TransferAsync(
        DownloadingItem downloading,
        IReadOnlyList<string> urls,
        string path,
        string localFileName,
        long expectedBytes)
    {
        return _transferBackend.TransferAsync(new DownloadTransferRequest(
            downloading,
            urls,
            path,
            localFileName,
            expectedBytes,
            () => EnsureDownloadIsActive(downloading),
            cancellationToken => ProjectionStore.UpdateDownloadingAsync(
                downloading,
                cancellationToken),
            CancellationToken.GetValueOrDefault()));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transferBackend.StartAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _transferBackend.StopAsync(cancellationToken).ConfigureAwait(true);
    }
}

internal enum DownloadTransferOutcome
{
    Failed,
    Succeeded,
    Paused
}
