using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.FileName;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Models;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

/// <summary>
/// 添加到下载列表服务
/// </summary>
internal sealed class AddToDownloadService : IAddToDownloadSession
{
    private readonly ILogger<AddToDownloadService> _logger;
    private IInfoService _videoInfoService = null!;
    private VideoInfoView? _videoInfoView;
    private IList<VideoSection>? _videoSections;
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IUserNotificationService _notificationService;
    private readonly IAppDialogService _dialogService;

    // 下载内容
    private bool _downloadAudio = true;
    private bool _downloadVideo = true;
    private bool _downloadDanmaku = true;
    private bool _downloadSubtitle = true;
    private bool _downloadCover = true;

    /// <summary>
    /// 添加下载
    /// </summary>
    /// <param name="streamType"></param>
    /// <param name="downloadLists"></param>
    /// <param name="projectionStore"></param>
    public AddToDownloadService(
        PlayStreamType streamType,
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        ISettingsStore settingsStore,
        IUserNotificationService notificationService,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        switch (streamType)
        {
            case PlayStreamType.Video:
                _videoInfoService = new VideoInfoService(null, settingsStore);
                break;
            case PlayStreamType.Bangumi:
                _videoInfoService = new BangumiInfoService(null, settingsStore);
                break;
            case PlayStreamType.Cheese:
                _videoInfoService = new CheeseInfoService(null, settingsStore);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 添加下载
    /// </summary>
    /// <param name="id"></param>
    /// <param name="streamType"></param>
    /// <param name="downloadLists"></param>
    /// <param name="projectionStore"></param>
    public AddToDownloadService(
        string id,
        PlayStreamType streamType,
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        ISettingsStore settingsStore,
        IUserNotificationService notificationService,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        switch (streamType)
        {
            case PlayStreamType.Video:
                _videoInfoService = new VideoInfoService(id, settingsStore);
                break;
            case PlayStreamType.Bangumi:
                _videoInfoService = new BangumiInfoService(id, settingsStore);
                break;
            case PlayStreamType.Cheese:
                _videoInfoService = new CheeseInfoService(id, settingsStore);
                break;
            default:
                break;
        }
    }

    public void SetVideoInfoService(IInfoService videoInfoService)
    {
        _videoInfoService = videoInfoService;
    }

    public void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections)
    {
        _videoInfoView = videoInfoView;
        _videoSections = videoSections;
    }

    public void GetVideo()
    {
        _videoInfoView = _videoInfoService.GetVideoView();
        if (_videoInfoView == null)
        {
            _logger.LogDebugMessage("VideoInfoView is null.");
            return;
        }

        _videoSections = _videoInfoService.GetVideoSections(true);
        if (_videoSections == null)
        {
            _logger.LogDebugMessage("Video sections do not exist.");

            _videoSections = new List<VideoSection>
            {
                new()
                {
                    Id = 0,
                    Title = "default",
                    IsSelected = true,
                    VideoPages = _videoInfoService.GetVideoPages() ?? new List<VideoPage>()
                }
            };
        }

        // 将所有视频设置为选中
        foreach (var section in _videoSections)
        {
            foreach (var item in section.VideoPages)
            {
                item.IsSelected = true;
            }
        }
    }

    /// <summary>
    /// 解析视频流
    /// </summary>
    /// <param name="videoInfoService"></param>
    public void ParseVideo(IInfoService videoInfoService)
    {
        ArgumentNullException.ThrowIfNull(videoInfoService);

        if (_videoSections == null)
        {
            return;
        }

        foreach (var section in _videoSections)
        {
            foreach (var page in section.VideoPages)
            {
                // 执行解析任务
                Utils.VideoPageInfo(videoInfoService.GetVideoStream(page), page, _settingsStore);
            }
        }
    }

    /// <summary>
    /// 选择文件夹和下载项
    /// </summary>
    public async Task<string?> SetDirectory(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 选择的下载文件夹
        var directory = string.Empty;

        // 是否使用默认下载目录
        var videoSettings = _settingsStore.Current.Video;
        if (videoSettings.IsUseSaveVideoRootPath == AllowStatus.Yes)
        {
            // 下载内容
            var videoContent = videoSettings.Content;
            _downloadAudio = videoContent.DownloadAudio;
            _downloadVideo = videoContent.DownloadVideo;
            _downloadDanmaku = videoContent.DownloadDanmaku;
            _downloadSubtitle = videoContent.DownloadSubtitle;
            _downloadCover = videoContent.DownloadCover;

            directory = videoSettings.SaveVideoRootPath;
        }
        else
        {
            // 打开文件夹选择器
            var result = await _dialogService.ShowAsync(
                new AppDialogRequest(AppDialog.DownloadSettings),
                cancellationToken).ConfigureAwait(true);
            if (result.Outcome == AppDialogOutcome.Accepted)
            {
                // 选择的下载文件夹
                directory = result.Parameters.TryGetValue("directory", out var directoryValue)
                    ? directoryValue as string ?? string.Empty
                    : string.Empty;

                // 下载内容
                _downloadAudio = GetBoolean(result.Parameters, "downloadAudio");
                _downloadVideo = GetBoolean(result.Parameters, "downloadVideo");
                _downloadDanmaku = GetBoolean(result.Parameters, "downloadDanmaku");
                _downloadSubtitle = GetBoolean(result.Parameters, "downloadSubtitle");
                _downloadCover = GetBoolean(result.Parameters, "downloadCover");
            }
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }


        if (!Directory.Exists(Directory.GetDirectoryRoot(directory)))
        {
            var alert = new AlertService(_dialogService);
            await alert
                .ShowError(DictionaryResource.GetString("DriveNotFound"), cancellationToken)
                .ConfigureAwait(true);

            directory = string.Empty;
        }

        // 下载设置dialog中如果点击取消或者关闭窗口，
        // 会返回空字符串，
        // 这时直接退出
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        // 文件夹不存在则创建
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    /// <summary>
    /// 添加到下载列表
    /// </summary>
    /// <param name="directory">下载路径</param>
    /// <param name="isAll">是否下载所有，包括未选中项</param>
    /// <returns>添加的数量</returns>
    public async Task<int> AddToDownload(
        string? directory,
        bool isAll = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(directory))
        {
            return -1;
        }

        if (_videoSections == null)
        {
            return -1;
        }

        if (_videoInfoView == null)
        {
            return -1;
        }

        var settings = _settingsStore.Current;

        // 视频计数
        var addedItems = new List<DownloadingItem>();
        // 添加到下载
        foreach (var section in _videoSections)
        {
            if (section.VideoPages == null)
            {
                continue;
            }

            foreach (var page in section.VideoPages)
            {
                // 只下载选中项，跳过未选中项
                if (!isAll && !page.IsSelected)
                {
                    continue;
                }

                // 没有解析的也跳过
                if (page.PlayUrl == null)
                {
                    continue;
                }

                // 判断VideoQuality
                var retry = 0;
                while (page.VideoQuality == null && retry < 5)
                {
                    // 执行解析任务
                    Utils.VideoPageInfo(
                        _videoInfoService.GetVideoStream(page, cancellationToken),
                        page,
                        _settingsStore);
                    retry++;
                }

                if (page.VideoQuality == null)
                {
                    continue;
                }

                var videoQuality = page.VideoQuality;
                var ownerMid = page.Owner?.Mid ?? -1;
                var ownerName = page.Owner?.Name ?? string.Empty;
                var audioCodec = Constant.GetAudioQualities().FirstOrDefault(t => t.Name == page.AudioQualityFormat) ?? new Quality();

                // 判断是否同一个视频，需要cid、画质、音质、视频编码都相同

                // 如果存在正在下载列表，则跳过，并提示
                var isDownloading = false;


                foreach (var item in _downloadLists.Downloading.Concat(addedItems))
                {
                    if (item.DownloadBase == null)
                    {
                        continue;
                    }

                    bool isSameVideo = item.DownloadBase.Cid == page.Cid &&
                                       item.Resolution.Id == videoQuality.Quality &&
                                       item.VideoCodecName == videoQuality.SelectedVideoCodec;

                    if (page.PlayUrl.Dash != null)
                    {
                        isSameVideo = isSameVideo && item.AudioCodec.Name == page.AudioQualityFormat;
                    }

                    if (isSameVideo)
                    {
                        _notificationService.Show(
                            $"{page.Name}{DictionaryResource.GetString("TipAlreadyToAddDownloading")}");
                        isDownloading = true;
                        break;
                    }
                }

                if (isDownloading)
                {
                    continue;
                }

                // TODO 如果存在下载完成列表，弹出选择框是否再次下载
                var isDownloaded = false;
                foreach (var item in _downloadLists.Downloaded)
                {
                    if (item.DownloadBase == null)
                    {
                        continue;
                    }

                    bool isSameVideo = item.DownloadBase.Cid == page.Cid &&
                                       item.Resolution.Id == videoQuality.Quality &&
                                       item.VideoCodecName == videoQuality.SelectedVideoCodec;

                    if (page.PlayUrl.Dash != null)
                    {
                        isSameVideo = isSameVideo && item.AudioCodec.Name == page.AudioQualityFormat;
                    }

                    if (isSameVideo)
                    {
                        var repeatDownloadStrategy = settings.Basic.RepeatDownloadStrategy;
                        switch (repeatDownloadStrategy)
                        {
                            case RepeatDownloadStrategy.Ask:
                                {
                                    var result = (await _dialogService.ShowAsync(
                                        new AppDialogRequest(
                                            AppDialog.AlreadyDownloaded,
                                            new Dictionary<string, object?>
                                            {
                                                ["message"] = $"{item.Name}已下载，是否重新下载"
                                            }),
                                        cancellationToken).ConfigureAwait(true)).Outcome;

                                    if (result == AppDialogOutcome.Accepted)
                                    {
                                        await _projectionStore
                                            .RemoveDownloadedAsync(item, cancellationToken)
                                            .ConfigureAwait(true);
                                        _downloadLists.Downloaded.Remove(item);
                                        isDownloaded = false;
                                    }
                                    else
                                    {
                                        isDownloaded = true;
                                    }

                                    break;
                                }
                            case RepeatDownloadStrategy.ReDownload:
                                isDownloaded = false;
                                break;
                            case RepeatDownloadStrategy.JumpOver:
                                isDownloaded = true;
                                break;
                            default:
                                isDownloaded = true;
                                break;
                        }

                        break;
                    }
                }

                if (isDownloaded)
                {
                    continue;
                }

                // 视频分区
                var zoneId = -1;
                var zoneList = VideoZone.Instance().Zones;
                var zone = zoneList.FirstOrDefault(it => it.Id == _videoInfoView?.TypeId);
                if (zone != null)
                {
                    if (zone.ParentId == 0)
                    {
                        zoneId = zone.Id;
                    }
                    else
                    {
                        var zoneParent = zoneList.FirstOrDefault(it => it.Id == zone.ParentId);
                        if (zoneParent != null)
                        {
                            zoneId = zoneParent.Id;
                        }
                    }
                }

                // 如果只有一个视频章节，则不在命名中出现
                var sectionName = string.Empty;
                if (_videoSections.Count > 1)
                {
                    sectionName = section.Title;
                }

                // 文件路径
                var fileNameParts = settings.Video.FileNameParts;
                var fileName = FileNameBuilder.Create(fileNameParts)
                    .SetSection(Format.FormatFileName(sectionName))
                    .SetMainTitle(Format.FormatFileName(_videoInfoView.Title))
                    .SetPageTitle(Format.FormatFileName(page.Name))
                    .SetVideoZone(_videoInfoView.VideoZone.Split('>')[0])
                    .SetAudioQuality(page.AudioQualityFormat)
                    .SetVideoQuality(videoQuality.QualityFormat)
                    .SetVideoCodec(
                        videoQuality.SelectedVideoCodec.Contains("AVC", StringComparison.Ordinal) ? "AVC" :
                        videoQuality.SelectedVideoCodec.Contains("HEVC", StringComparison.Ordinal) ? "HEVC" :
                        videoQuality.SelectedVideoCodec.Contains("Dolby", StringComparison.Ordinal) ? "Dolby Vision" :
                        videoQuality.SelectedVideoCodec.Contains("AV1", StringComparison.Ordinal) ? "AV1" : "")
                    .SetVideoPublishTime(page.PublishTime)
                    .SetAvid(page.Avid)
                    .SetBvid(page.Bvid)
                    .SetCid(page.Cid)
                    .SetUpMid(ownerMid)
                    .SetUpName(Format.FormatFileName(ownerName));

                // 序号设置
                var orderFormat = settings.Video.OrderFormat;
                switch (orderFormat)
                {
                    case OrderFormat.Natural:
                        fileName.SetOrder(page.Order);
                        break;
                    case OrderFormat.LeadingZeros:
                        fileName.SetOrder(page.Order, section.VideoPages.Count);
                        break;
                }

                // 合成绝对路径
                var filePath = Path.Combine(directory, fileName.RelativePath());

                if (settings.Basic.RepeatFileAutoAddNumberSuffix)
                {
                    // 如果存在同名文件，自动重命名
                    // todo 如果重新下载呢。还没想好
                    var directoryName = Path.GetDirectoryName(filePath);
                    if (Directory.Exists(directoryName))
                    {
                        var files = Directory.GetFiles(directoryName).Select(Path.GetFileNameWithoutExtension).Distinct().ToList();

                        if (files.Contains(Path.GetFileNameWithoutExtension(filePath)))
                        {
                            var count = 1;
                            var newFilePath = filePath;
                            while (files.Contains(Path.GetFileNameWithoutExtension(newFilePath)))
                            {
                                newFilePath = Path.Combine(directory, $"{fileName.RelativePath()}({count})");
                                count++;
                            }

                            filePath = newFilePath;
                        }
                    }
                }

                // 视频类别
                PlayStreamType playStreamType;
                switch (_videoInfoView.TypeId)
                {
                    case -10:
                        playStreamType = PlayStreamType.Cheese;
                        break;
                    case 13:
                    case 23:
                    case 177:
                    case 167:
                    case 11:
                        playStreamType = PlayStreamType.Bangumi;
                        break;
                    case 1:
                    case 3:
                    case 129:
                    case 4:
                    case 36:
                    case 188:
                    case 234:
                    case 223:
                    case 160:
                    case 211:
                    case 217:
                    case 119:
                    case 155:
                    case 202:
                    case 5:
                    case 181:
                    default:
                        playStreamType = PlayStreamType.Video;
                        break;
                }

                // 如果不存在，直接添加到下载列表
                var downloadBase = new DownloadBase
                {
                    Bvid = page.Bvid,
                    Avid = page.Avid,
                    Cid = page.Cid,
                    EpisodeId = page.EpisodeId,
                    CoverUrl = _videoInfoView.CoverUrl,
                    PageCoverUrl = page.FirstFrame,
                    ZoneId = zoneId,
                    FilePath = filePath,
                    Order = page.Order,
                    MainTitle = _videoInfoView.Title,
                    Name = page.Name,
                    Duration = page.Duration,
                    VideoCodecName = videoQuality.SelectedVideoCodec,
                    Resolution = new Quality { Name = videoQuality.QualityFormat, Id = videoQuality.Quality },
                    AudioCodec = audioCodec,
                    Page = page.Page
                };
                var downloading = new Downloading
                {
                    PlayStreamType = playStreamType,
                    DownloadStatus = DownloadStatus.NotStarted,
                };

                // 需要下载的内容
                downloadBase.NeedDownloadContent["downloadAudio"] = _downloadAudio;
                downloadBase.NeedDownloadContent["downloadVideo"] = _downloadVideo;
                downloadBase.NeedDownloadContent["downloadDanmaku"] = _downloadDanmaku;
                downloadBase.NeedDownloadContent["downloadSubtitle"] = _downloadSubtitle;
                downloadBase.NeedDownloadContent["downloadCover"] = _downloadCover;

                var downloadingItem = new DownloadingItem
                {
                    DownloadBase = downloadBase,
                    Downloading = downloading,
                    PlayUrl = page.PlayUrl,
                };

                if (settings.Video.Content.GenerateMovieMetadata && _downloadVideo)
                {
                    downloadingItem.Metadata = BuildMovieMetadata(page);
                }

                await _projectionStore
                    .AddDownloadingAsync(downloadingItem, cancellationToken)
                    .ConfigureAwait(true);
                addedItems.Add(downloadingItem);
            }
        }

        if (addedItems.Count > 0)
        {
            _downloadLists.Downloading.AddRange(addedItems);
        }

        return addedItems.Count;
    }

    private static bool GetBoolean(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) && value is true;
    }

    private MovieMetadata BuildMovieMetadata(VideoPage page)
    {
        var score = _videoInfoView?.Score;
        var metadata = new MovieMetadata
        {
            Title = page.Name,
            Plot = _videoInfoView?.Description ?? string.Empty,
            Year = page.OriginalPublishTime.Year.ToString(CultureInfo.InvariantCulture),
            Premiered = page.OriginalPublishTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BilibiliId = new UniqueId("bilibili", page.Bvid)
        };

        metadata.Actors.Add(new Actor(page.Owner?.Name ?? string.Empty, (page.Owner?.Mid ?? -1).ToString(CultureInfo.InvariantCulture)));
        foreach (var genre in _videoInfoView?.VideoZone?.Split(">") ?? Array.Empty<string>())
        {
            metadata.Genres.Add(genre);
        }

        foreach (var tag in page.LazyTags?.Value ?? Enumerable.Empty<string>())
        {
            metadata.Tags.Add(tag);
        }

        if (score != null)
        {
            metadata.Ratings.Add(new Rating("bilibili", score.Value, isDefault: true));
        }

        return metadata;
    }
}
