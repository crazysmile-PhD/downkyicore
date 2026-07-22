using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
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
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPipeline : IDisposable
{
    private bool _disposed;
    private string Tag => _transferBackend.Name;

    private IUserNotificationService NotificationService { get; }
    private DownloadListState DownloadLists { get; }
    private ImmutableObservableCollection<DownloadingItem> DownloadingList { get; }
    private ImmutableObservableCollection<DownloadedItem> DownloadedList { get; }
    private DownloadTaskProjectionStore ProjectionStore { get; }
    private IUiDispatcher UiDispatcher { get; }
    private DownloadDiagnosticLogger DiagnosticLogger { get; }
    private FfmpegProcessor FfmpegProcessor { get; }
    private DownloadArtifactWriter ArtifactWriter { get; }
    private DownloadTaskStateWriter StateWriter { get; }
    private ISettingsStore SettingsStore { get; }
    private IWbiKeyProvider WbiKeyProvider { get; }
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
        IWbiKeyProvider wbiKeyProvider,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        DownloadArtifactWriter artifactWriter,
        DownloadTaskStateWriter stateWriter,
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
        WbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        DiagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        FfmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        ArtifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        StateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
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

    private Task<string?> DownloadAudioAsync(
        DownloadingItem downloading,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        return DownloadMediaAsync(downloading, BaseDownloadAudio(downloading), settings);
    }

    private Task<string?> DownloadVideoAsync(
        DownloadingItem downloading,
        ApplicationSettings settings)
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
            }, settings);
    }

    private async Task<string?> DownloadMediaAsync(
        DownloadingItem downloading,
        PlayUrlDashVideo? media,
        ApplicationSettings settings)
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
                await StateWriter.UpdateAsync(
                    downloading,
                    CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
            }
        }
        else if (downloading.Downloading.DownloadFiles.TryAdd(key, fileName))
        {
            downloading.Downloading.Gid = null;
            await StateWriter.UpdateAsync(
                downloading,
                CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
        }

        NormalizeTransferSchemes(urls, settings.Network.UseSsl == AllowStatus.Yes);
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
            await StateWriter.UpdateAsync(
                downloading,
                CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
            return targetFile;
        }

        if (outcome != DownloadTransferOutcome.Paused)
        {
            downloading.Downloading.Gid = null;
            DeleteInvalidDownloadedMediaFile(targetFile);
            await StateWriter.UpdateAsync(
                downloading,
                CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
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

    private async Task<string?> MixedFlowAsync(
        DownloadingItem downloading,
        string? audioUid,
        string? videoUid,
        VideoApplicationSettings videoSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        EnsureDownloadIsActive(downloading);

        // 更新状态显示
        downloading.DownloadStatusTitle = DictionaryResource.GetString("MixedFlow");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingVideo");
        // 下载大小
        downloading.DownloadingFileSize = string.Empty;
        // 下载速度
        downloading.SpeedDisplay = string.Empty;

        var finalFile = $"{downloading.DownloadBase.FilePath}.mp4";
        if (videoUid == null)
        {
            finalFile = videoSettings.IsTranscodingAacToMp3 == AllowStatus.Yes
                ? $"{downloading.DownloadBase.FilePath}.mp3"
                : downloading.AudioCodec.Id == 30251
                    ? $"{downloading.DownloadBase.FilePath}.flac"
                    : $"{downloading.DownloadBase.FilePath}.aac";
        }

        // 合并音视频
        var succeeded = await FfmpegProcessor
            .MergeVideoAsync(videoSettings, audioUid, videoUid, finalFile, cancellationToken)
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
        VideoApplicationSettings videoSettings,
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
            .ConcatDurlVideosAsync(
                videoSettings,
                segments,
                finalFile,
                cancellationToken: cancellationToken)
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


    private async Task ParseAsync(
        DownloadingItem downloading,
        ApplicationSettings settings)
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
                playUrl = downloading.PlayUrl ?? await WbiRequestExecutor.ExecuteAsync(
                    WbiKeyProvider,
                    (keys, unixTimeSeconds) => settings.Video.VideoParseType switch
                {
                    0 => VideoStreamApi.GetVideoPlayUrl(keys, unixTimeSeconds, downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                        cancellationToken: CancellationToken.GetValueOrDefault()),
                    1 => VideoStreamApi.GetVideoPlayUrlWebPage(keys, unixTimeSeconds, downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid, downloading.DownloadBase.Cid,
                        downloading.DownloadBase.Page, CancellationToken.GetValueOrDefault()),
                    _ => throw new ArgumentException("Invalid video parse type. Valid values are: 0 (WebAPI) or 1 (WebPage).")
                },
                    TimeProvider.System,
                    CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
                break;
            case PlayStreamType.Bangumi:
                playUrl = downloading.PlayUrl ?? VideoStreamApi.GetBangumiPlayUrl(downloading.DownloadBase.Avid, downloading.DownloadBase.Bvid,
                    downloading.DownloadBase.Cid, downloading.DownloadBase.EpisodeId,
                    cancellationToken: CancellationToken.GetValueOrDefault());
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
        var settings = SettingsStore.Current;
        downloading.DownloadBase.FilePath = downloading.DownloadBase.FilePath.Replace("\\", "/", StringComparison.Ordinal);
        var path = GetDownloadDirectoryPath(downloading.DownloadBase.FilePath);

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

                // 解析并依次下载音频、视频、弹幕、字幕、封面等内容
                await ParseAsync(downloading, settings).ConfigureAwait(true);

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
                            audioUid = await DownloadAudioAsync(downloading, settings).ConfigureAwait(true);
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
                            videoUid = await DownloadVideoAsync(downloading, settings).ConfigureAwait(true);
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
                        outputMedia = await MixedFlowAsync(
                            downloading,
                            audioUid,
                            videoUid,
                            settings.Video,
                            cancellationToken).ConfigureAwait(true);
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
                                    var result = await DownloadVideoAsync(
                                        downloading,
                                        settings).ConfigureAwait(true);
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
                                    settings.Video,
                                    cancellationToken)
                                .ConfigureAwait(true);
                            isMediaSuccess = concatResult.Succeeded;
                        }
                        else
                        {
                            var outputMedia = await MixedFlowAsync(
                                downloading,
                                null,
                                downloadStatus[0].FilePath,
                                settings.Video,
                                cancellationToken).ConfigureAwait(true);
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
                if (settings.Video.Content.GenerateMovieMetadata)
                {
                    ArtifactWriter.GenerateNfoFile(downloading);
                }

                string? outputDanmaku = null;
                // 如果需要下载弹幕
                if (downloading.DownloadBase.NeedDownloadContent["downloadDanmaku"])
                {
                    outputDanmaku = await ArtifactWriter.DownloadDanmakuAsync(
                        downloading,
                        settings.Danmaku,
                        cancellationToken).ConfigureAwait(true);
                }

                // 暂停
                EnsureDownloadIsActive(downloading);

                IReadOnlyList<string>? outputSubtitles = null;
                // 如果需要下载字幕
                if (downloading.DownloadBase.NeedDownloadContent["downloadSubtitle"])
                {
                    outputSubtitles = await ArtifactWriter.DownloadSubtitleAsync(
                        downloading,
                        cancellationToken).ConfigureAwait(true);
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
                    outputPageCover = await ArtifactWriter.DownloadCoverAsync(
                        downloading,
                        downloading.DownloadBase.PageCoverUrl,
                        pageCoverFileName,
                        cancellationToken).ConfigureAwait(true);


                    var coverFileName = $"{downloading.DownloadBase.FilePath}.Cover.{GetImageExtension(downloading.DownloadBase.CoverUrl)}";
                    // 封面
                    outputCover = await ArtifactWriter.DownloadCoverAsync(
                        downloading,
                        downloading.DownloadBase.CoverUrl,
                        coverFileName,
                        cancellationToken).ConfigureAwait(true);
                }

                // 暂停
                EnsureDownloadIsActive(downloading);

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
                    var finishedSort = settings.Basic.DownloadFinishedSort;
                    DownloadLists.SortDownloaded(finishedSort);
                }).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException e)
        {
            Logger.LogDebugMessage(e.Message);
            await StateWriter.UpdateAsync(
                downloading,
                System.Threading.CancellationToken.None).ConfigureAwait(true);
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
        await StateWriter.UpdateAsync(
            downloading,
            CancellationToken.GetValueOrDefault()).ConfigureAwait(true);
    }

    /// <summary>
    /// 获取图片的扩展名
    /// </summary>
    /// <param name="coverUrl"></param>
    /// <returns></returns>
    internal static string GetImageExtension(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return string.Empty;
        }

        var candidate = coverUrl.StartsWith("//", StringComparison.Ordinal)
            ? $"{Uri.UriSchemeHttps}:{coverUrl}"
            : coverUrl;
        var path = Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : coverUrl.Split('?', '#')[0];
        return Path.GetExtension(path).TrimStart('.');
    }

    internal static string GetDownloadDirectoryPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetDirectoryName(filePath)
               ?? throw new ArgumentException("Download file path must include a directory.", nameof(filePath));
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

            await StateWriter.UpdateAsync(
                item,
                System.Threading.CancellationToken.None).ConfigureAwait(true);
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
            cancellationToken => StateWriter.UpdateAsync(
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
