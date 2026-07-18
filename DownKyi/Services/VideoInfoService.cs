using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;
using VideoPage = DownKyi.ViewModels.PageViewModels.VideoPage;

namespace DownKyi.Services;

internal class VideoInfoService : IInfoService
{
    private readonly VideoView? _videoView;
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoTagProvider _tagProvider;
    private readonly IWbiKeyProvider _wbiKeyProvider;

    public VideoInfoService(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
    }

    internal VideoInfoService(
        VideoView videoView,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider)
    {
        _videoView = videoView ?? throw new ArgumentNullException(nameof(videoView));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
    }

    public static async Task<VideoInfoService> CreateAsync(
        string input,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(tagProvider);
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);

        VideoView? videoView = null;
        if (ParseEntrance.IsAvId(input) || ParseEntrance.IsAvUrl(input))
        {
            var avid = ParseEntrance.GetAvId(input);
            videoView = await LoadVideoViewAsync(
                wbiKeyProvider,
                bvid: null,
                avid,
                cancellationToken).ConfigureAwait(false);
        }
        else if (ParseEntrance.IsBvId(input) || ParseEntrance.IsBvUrl(input))
        {
            var bvid = ParseEntrance.GetBvId(input);
            videoView = await LoadVideoViewAsync(
                wbiKeyProvider,
                bvid,
                avid: -1,
                cancellationToken).ConfigureAwait(false);
        }

        return videoView == null
            ? new VideoInfoService(settingsStore, tagProvider, wbiKeyProvider)
            : new VideoInfoService(videoView, settingsStore, tagProvider, wbiKeyProvider);
    }

    private static Task<VideoView?> LoadVideoViewAsync(
        IWbiKeyProvider wbiKeyProvider,
        string? bvid,
        long avid,
        CancellationToken cancellationToken)
    {
        return WbiRequestExecutor.ExecuteAsync(
            wbiKeyProvider,
            (keys, unixTimeSeconds) => VideoInfo.VideoViewInfo(
                keys,
                unixTimeSeconds,
                bvid,
                avid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken);
    }

    /// <summary>
    /// 获取视频剧集
    /// </summary>
    /// <returns></returns>
    public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_videoView?.Pages == null)
        {
            return null;
        }

        if (_videoView.Pages.Count == 0)
        {
            return null;
        }

        var videoPages = new List<VideoPage>();

        var timeFormat = _settingsStore.Current.Video.FileNamePartTimeFormat;
        var order = 0;
        foreach (var page in _videoView.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;

            // 标题
            string name;
            if (_videoView.Pages.Count == 1)
            {
                name = _videoView.Title;
            }
            else
            {
                //name = page.part;
                if (string.IsNullOrEmpty(page.Part))
                {
                    // 如果page.part为空字符串
                    name = $"{_videoView.Title}-P{order}";
                }
                else
                {
                    name = page.Part;
                }
            }

            var videoPage = new VideoPage
            {
                Avid = _videoView.Aid,
                Bvid = _videoView.Bvid,
                Cid = page.Cid,
                EpisodeId = -1,
                FirstFrame = page.FirstFrame,
                Order = order,
                Name = name,
                Duration = "N/A",
                Page = page.Page,
                LoadTagsAsync = currentToken =>
                    _tagProvider.GetTagsAsync(_videoView.Bvid, page.Cid, currentToken)
            };

            // UP主信息
            videoPage.Owner = _videoView.Owner;
            if (videoPage.Owner == null)
            {
                videoPage.Owner = new VideoOwner
                {
                    Name = "",
                    Face = "",
                    Mid = -1,
                };
            }

            // 视频发布时间
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
            var dateTime = startTime.AddSeconds(_videoView.Pubdate);
            videoPage.OriginalPublishTime = dateTime;
            videoPage.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);

            videoPages.Add(videoPage);
        }

        return videoPages;
    }

    /// <summary>
    /// 获取视频章节与剧集
    /// </summary>
    /// <returns></returns>
    public IList<VideoSection>? GetVideoSections(bool noUgc = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!(_videoView?.UgcSeason?.Sections?.Count > 0)) return null;
        var videoSections = new List<VideoSection>();

        // 不需要ugc内容
        if (noUgc)
        {
            videoSections.Add(CreateDefaultVideoSection(cancellationToken));
            return videoSections;
        }

        var timeFormat = _settingsStore.Current.Video.FileNamePartTimeFormat;
        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);

        foreach (var section in _videoView.UgcSeason.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pages = new List<VideoPage>();
            var order = 0;
            foreach (var episode in section.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (episode.Pages?.Count > 1)
                {
                    var videoSection = CreateVideoSectionFromEpisode(section, episode, startTime, timeFormat);
                    videoSections.Add(videoSection);
                }
                else
                {
                    pages.Add(GenerateVideoPage(episode, ++order, startTime, timeFormat));
                }
            }

            if (pages.Count <= 0) continue;
            {
                var videoSection = new VideoSection
                {
                    Id = section.Id,
                    Title = section.Title,
                    VideoPages = pages
                };
                videoSections.Add(videoSection);
            }
        }

        if (videoSections.Count > 0)
        {
            videoSections[0].IsSelected = true;
        }

        return videoSections;
    }

    private VideoSection CreateDefaultVideoSection(CancellationToken cancellationToken)
    {
        return new VideoSection
        {
            Id = 0,
            Title = "default",
            IsSelected = true,
            VideoPages = GetVideoPages(cancellationToken) ?? new List<VideoPage>()
        };
    }

    private VideoSection CreateVideoSectionFromEpisode(UgcSection section, UgcEpisode episode, DateTime startTime, string timeFormat)
    {
        var videoSection = new VideoSection
        {
            Id = section.Id,
            Title = episode.Title,
            VideoPages = new List<VideoPage>()
        };
        var owner = _videoView?.Owner ?? new VideoOwner { Name = string.Empty, Face = string.Empty, Mid = -1 };
        var order = 1;
        foreach (var p in episode.Pages)
        {
            var dateTime = startTime.AddSeconds(episode.Arc.Ctime);
            videoSection.VideoPages.Add(new VideoPage
            {
                Avid = episode.Aid,
                Bvid = episode.Bvid,
                Cid = p.Cid,
                EpisodeId = -1,
                FirstFrame = episode.Arc.Pic,
                Order = order++,
                Name = p.Part,
                Duration = "N/A",
                Owner = owner,
                Page = p.Page,
                PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture),
                OriginalPublishTime = dateTime,
                LoadTagsAsync = currentToken =>
                    _tagProvider.GetTagsAsync(episode.Bvid, p.Cid, currentToken)
            });
        }

        return videoSection;
    }

    private VideoPage GenerateVideoPage(UgcEpisode episode, int order, DateTime startTime, string timeFormat)
    {
        var page = new VideoPage
        {
            Avid = episode.Aid,
            Bvid = episode.Bvid,
            Cid = episode.Cid,
            EpisodeId = -1,
            FirstFrame = episode.Arc.Pic,
            Order = order,
            Name = episode.Title,
            Duration = "N/A",
            Owner = _videoView?.Owner ?? new VideoOwner { Name = "", Face = "", Mid = -1 },
            Page = episode.Page.Page,
            LoadTagsAsync = currentToken =>
                _tagProvider.GetTagsAsync(episode.Bvid, episode.Cid, currentToken)
        };
        var dateTime = startTime.AddSeconds(episode.Arc.Ctime);
        page.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);
        page.OriginalPublishTime = dateTime;
        return page;
    }

    /// <summary>
    /// 获取视频流的信息，从VideoPage返回
    /// </summary>
    /// <param name="page"></param>
    public async Task<PlayUrl?> GetVideoStreamAsync(
        VideoPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        var videoParseType = _settingsStore.Current.Video.VideoParseType;
        return await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => videoParseType switch
        {
            0 => VideoStreamApi.GetVideoPlayUrl(
                keys,
                unixTimeSeconds,
                page.Avid,
                page.Bvid,
                page.Cid,
                cancellationToken: cancellationToken),
            1 => VideoStreamApi.GetVideoPlayUrlWebPage(
                keys,
                unixTimeSeconds,
                page.Avid,
                page.Bvid,
                page.Cid,
                page.Page,
                cancellationToken),
            _ => null
        },
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取视频信息
    /// </summary>
    /// <returns></returns>
    public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var videoView = _videoView;
        if (videoView == null)
        {
            return null;
        }

        // 查询、保存封面
        var coverUrl = videoView.Pic;

        // 分区
        var videoZone = string.Empty;
        var zoneList = VideoZone.Instance().Zones;
        var zone = zoneList.FirstOrDefault(it => it.Id == videoView.Tid);
        if (zone != null)
        {
            var zoneParent = zoneList.FirstOrDefault(it => it.Id == zone.ParentId);
            if (zoneParent != null)
            {
                videoZone = zoneParent.Name + ">" + zone.Name;
            }
            else
            {
                videoZone = zone.Name;
            }
        }
        else
        {
            videoZone = videoView.Tname;
        }

        // 获取用户头像
        string upName;
        if (videoView.Owner != null)
        {
            upName = videoView.Owner.Name;
        }
        else
        {
            upName = "";
        }

        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
        var dateTime = startTime.AddSeconds(videoView.Pubdate);
        var videoInfoView = new VideoInfoView
        {
            CoverUrl = coverUrl ?? string.Empty,
            Title = videoView.Title,
            TypeId = videoView.Tid,
            VideoZone = videoZone,
            CreateTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            PlayNumber = Format.FormatNumber(videoView.Stat.View),
            DanmakuNumber = Format.FormatNumber(videoView.Stat.Danmaku),
            LikeNumber = Format.FormatNumber(videoView.Stat.Like),
            CoinNumber = Format.FormatNumber(videoView.Stat.Coin),
            FavoriteNumber = Format.FormatNumber(videoView.Stat.Favorite),
            ShareNumber = Format.FormatNumber(videoView.Stat.Share),
            ReplyNumber = Format.FormatNumber(videoView.Stat.Reply),
            Description = videoView.Desc,
            UpHeader = videoView.Owner?.Face ?? string.Empty,
            UpperMid = videoView.Owner?.Mid ?? -1,
            UpName = upName
        };

        return videoInfoView;
    }
}
