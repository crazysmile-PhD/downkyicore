using System;
using System.Collections.Generic;
using System.Globalization;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Cheese;
using DownKyi.Core.BiliApi.Cheese.Models;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services;

internal class CheeseInfoService : IInfoService
{
    private readonly CheeseView? _cheeseView;
    private readonly ISettingsStore _settingsStore;

    public CheeseInfoService(
        string? input,
        ISettingsStore settingsStore,
        System.Threading.CancellationToken cancellationToken = default)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        if (input == null)
        {
            return;
        }

        if (ParseEntrance.IsCheeseSeasonUrl(input))
        {
            var seasonId = ParseEntrance.GetCheeseSeasonId(input);
            _cheeseView = CheeseInfo.CheeseViewInfo(seasonId, cancellationToken: cancellationToken);
        }

        if (ParseEntrance.IsCheeseEpisodeUrl(input))
        {
            var episodeId = ParseEntrance.GetCheeseEpisodeId(input);
            _cheeseView = CheeseInfo.CheeseViewInfo(-1, episodeId, cancellationToken);
        }
    }

    /// <summary>
    /// 获取视频剧集
    /// </summary>
    /// <returns></returns>
    public IList<VideoPage> GetVideoPages(System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = new List<VideoPage>();
        if (_cheeseView == null)
        {
            return pages;
        }

        if (_cheeseView.Episodes == null)
        {
            return pages;
        }

        if (_cheeseView.Episodes.Count == 0)
        {
            return pages;
        }

        var order = 0;
        foreach (var episode in _cheeseView.Episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;
            var name = episode.Title;

            var duration = Format.FormatDuration(episode.Duration - 1);

            var page = new VideoPage
            {
                Avid = episode.Aid,
                Bvid = string.Empty,
                Cid = episode.Cid,
                EpisodeId = episode.Id,
                FirstFrame = episode.Cover,
                Order = order,
                Name = name,
                Duration = "N/A"
            };

            // UP主信息
            if (_cheeseView.UpInfo != null)
            {
                page.Owner = new VideoOwner
                {
                    Name = _cheeseView.UpInfo.Name,
                    Face = _cheeseView.UpInfo.Avatar,
                    Mid = _cheeseView.UpInfo.Mid,
                };
            }
            else
            {
                page.Owner = new VideoOwner
                {
                    Name = "",
                    Face = "",
                    Mid = -1,
                };
            }

            // 文件命名中的时间格式
            var timeFormat = _settingsStore.Settings.GetFileNamePartTimeFormat();
            // 视频发布时间
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
            var dateTime = startTime.AddSeconds(episode.ReleaseDate);
            page.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);
            page.OriginalPublishTime = dateTime;
            pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// 获取视频章节与剧集
    /// </summary>
    /// <returns></returns>
    public IList<VideoSection>? GetVideoSections(bool noUgc = false, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// 获取视频流的信息，从VideoPage返回
    /// </summary>
    /// <param name="page"></param>
    public PlayUrl? GetVideoStream(VideoPage page, System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        return VideoStreamApi.GetCheesePlayUrl(page.Avid, page.Bvid, page.Cid, page.EpisodeId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 获取视频信息
    /// </summary>
    /// <returns></returns>
    public VideoInfoView? GetVideoView(System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cheeseView == null)
        {
            return null;
        }

        // 查询、保存封面
        // 将SeasonId保存到avid字段中
        // 每集封面的cid保存到cid字段，EpisodeId保存到bvid字段中
        var coverUrl = _cheeseView.Cover;

        // 获取用户头像
        string upName;
        if (_cheeseView.UpInfo != null)
        {
            upName = _cheeseView.UpInfo.Name;
        }
        else
        {
            upName = "";
        }

        var videoInfoView = new VideoInfoView
        {
            CoverUrl = coverUrl ?? string.Empty,
            Title = _cheeseView.Title,
            TypeId = -10,
            VideoZone = DictionaryResource.GetString("Cheese"),
            CreateTime = string.Empty,
            PlayNumber = Format.FormatNumber(_cheeseView.Stat.Play),
            DanmakuNumber = Format.FormatNumber(0),
            LikeNumber = Format.FormatNumber(0),
            CoinNumber = Format.FormatNumber(0),
            FavoriteNumber = Format.FormatNumber(0),
            ShareNumber = Format.FormatNumber(0),
            ReplyNumber = Format.FormatNumber(0),
            Description = _cheeseView.Subtitle,
            UpName = upName,
            UpHeader = _cheeseView.UpInfo?.Avatar ?? string.Empty,
            UpperMid = _cheeseView.UpInfo?.Mid ?? -1
        };

        return videoInfoView;
    }
}
