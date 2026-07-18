using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Bangumi;
using DownKyi.Core.BiliApi.Bangumi.Models;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services;

internal class BangumiInfoService : IInfoService
{
    private readonly BangumiSeason? _bangumiSeason;
    private readonly ISettingsStore _settingsStore;

    public BangumiInfoService(
        string? input,
        ISettingsStore settingsStore,
        CancellationToken cancellationToken = default)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        if (input == null)
        {
            return;
        }

        if (ParseEntrance.IsBangumiSeasonId(input) || ParseEntrance.IsBangumiSeasonUrl(input))
        {
            var seasonId = ParseEntrance.GetBangumiSeasonId(input);
            _bangumiSeason = BangumiInfo.BangumiSeasonInfo(seasonId, cancellationToken: cancellationToken);
        }

        if (ParseEntrance.IsBangumiEpisodeId(input) || ParseEntrance.IsBangumiEpisodeUrl(input))
        {
            var episodeId = ParseEntrance.GetBangumiEpisodeId(input);
            _bangumiSeason = BangumiInfo.BangumiSeasonInfo(-1, episodeId, cancellationToken);
        }

        if (ParseEntrance.IsBangumiMediaId(input) || ParseEntrance.IsBangumiMediaUrl(input))
        {
            var mediaId = ParseEntrance.GetBangumiMediaId(input);
            var bangumiMedia = BangumiInfo.BangumiMediaInfo(mediaId, cancellationToken);
            if (bangumiMedia != null)
            {
                _bangumiSeason = BangumiInfo.BangumiSeasonInfo(bangumiMedia.SeasonId, cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// 获取视频剧集
    /// </summary>
    /// <returns></returns>
    public IList<VideoPage> GetVideoPages(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = new List<VideoPage>();
        if (_bangumiSeason == null)
        {
            return pages;
        }

        if (_bangumiSeason.Episodes == null)
        {
            return pages;
        }

        if (_bangumiSeason.Episodes.Count == 0)
        {
            return pages;
        }

        var order = 0;
        foreach (var episode in _bangumiSeason.Episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;

            // 标题
            string name;

            // 将share_copy作为name，删除《》中的标题
            name = Regex.Replace(episode.ShareCopy, @"^《.*?》", "");

            // 删除前后空白符
            name = name.Trim();

            var page = new VideoPage
            {
                Avid = episode.Aid,
                Bvid = episode.Bvid,
                Cid = episode.Cid,
                EpisodeId = episode.EpisodeId,
                FirstFrame = episode.Cover,
                Order = order,
                Name = name,
                Duration = "N/A",
                LoadTagsAsync = CreateLocalTagLoader(_bangumiSeason.Styles)
            };

            // UP主信息
            if (_bangumiSeason.UpInfo != null)
            {
                page.Owner = new VideoOwner
                {
                    Name = _bangumiSeason.UpInfo.Name,
                    Face = _bangumiSeason.UpInfo.Avatar,
                    Mid = _bangumiSeason.UpInfo.Mid,
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
            var timeFormat = _settingsStore.Current.Video.FileNamePartTimeFormat;
            // 视频发布时间
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
            var dateTime = startTime.AddSeconds(episode.PubTime);
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
    public IList<VideoSection>? GetVideoSections(bool noUgc = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bangumiSeason == null)
        {
            return null;
        }

        var videoSections = new List<VideoSection>
        {
            new()
            {
                Id = _bangumiSeason.Positive.Id,
                Title = _bangumiSeason.Positive.Title,
                IsSelected = true,
                VideoPages = GetVideoPages(cancellationToken)
            }
        };

        // 不需要其他季或花絮内容
        if (noUgc)
        {
            return videoSections;
        }

        if (_bangumiSeason.Section == null)
        {
            return null;
        }

        if (_bangumiSeason.Section.Count == 0)
        {
            return null;
        }

        foreach (var section in _bangumiSeason.Section)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pages = new List<VideoPage>();
            var order = 0;
            foreach (var episode in section.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                order++;

                // 标题
                var name = !string.IsNullOrEmpty(episode.LongTitle) ? $"{episode.Title} {episode.LongTitle}" : episode.Title;
                var page = new VideoPage
                {
                    Avid = episode.Aid,
                    Bvid = episode.Bvid,
                    Cid = episode.Cid,
                    EpisodeId = episode.EpisodeId,
                    FirstFrame = episode.Cover,
                    Order = order,
                    Name = name,
                    Duration = "N/A",
                    LoadTagsAsync = CreateLocalTagLoader(_bangumiSeason.Styles)
                };

                // UP主信息
                if (_bangumiSeason.UpInfo != null)
                {
                    page.Owner = new VideoOwner
                    {
                        Name = _bangumiSeason.UpInfo.Name,
                        Face = _bangumiSeason.UpInfo.Avatar,
                        Mid = _bangumiSeason.UpInfo.Mid,
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
                var timeFormat = _settingsStore.Current.Video.FileNamePartTimeFormat;
                // 视频发布时间
                var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
                var dateTime = startTime.AddSeconds(episode.PubTime);
                page.OriginalPublishTime = dateTime;
                page.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);
                pages.Add(page);
            }

            var videoSection = new VideoSection
            {
                Id = section.Id,
                Title = section.Title,
                VideoPages = pages
            };
            videoSections.Add(videoSection);
        }

        return videoSections;
    }

    private static Func<CancellationToken, Task<IReadOnlyList<string>>> CreateLocalTagLoader(
        IEnumerable<string> tags)
    {
        var snapshot = tags.ToArray();
        return cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(snapshot);
        };
    }

    /// <summary>
    /// 获取视频流的信息，从VideoPage返回
    /// </summary>
    /// <param name="page"></param>
    public Task<PlayUrl?> GetVideoStreamAsync(VideoPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VideoStreamApi.GetBangumiPlayUrl(
            page.Avid,
            page.Bvid,
            page.Cid,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 获取视频信息
    /// </summary>
    /// <returns></returns>
    public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bangumiSeason == null)
        {
            return null;
        }

        // 查询、保存封面
        // 将SeasonId保存到avid字段中
        // 每集封面的cid保存到cid字段，EpisodeId保存到bvid字段中
        var coverUrl = _bangumiSeason.Cover;

        // 获取用户头像
        string upName;
        if (_bangumiSeason.UpInfo != null)
        {
            upName = _bangumiSeason.UpInfo.Name;
        }
        else
        {
            upName = "";
        }

        var videoInfoView = new VideoInfoView
        {
            CoverUrl = coverUrl ?? string.Empty,
            Title = _bangumiSeason.Title,
            TypeId = BangumiType.TypeId[_bangumiSeason.Type],
            VideoZone = DictionaryResource.GetString(BangumiType.Type[_bangumiSeason.Type]),
            PlayNumber = Format.FormatNumber(_bangumiSeason.Stat.Views),
            DanmakuNumber = Format.FormatNumber(_bangumiSeason.Stat.Danmakus),
            LikeNumber = Format.FormatNumber(_bangumiSeason.Stat.Likes),
            CoinNumber = Format.FormatNumber(_bangumiSeason.Stat.Coins),
            FavoriteNumber = Format.FormatNumber(_bangumiSeason.Stat.Favorites),
            ShareNumber = Format.FormatNumber(_bangumiSeason.Stat.Share),
            ReplyNumber = Format.FormatNumber(_bangumiSeason.Stat.Reply),
            Description = _bangumiSeason.Evaluate,
            Score = _bangumiSeason.Rating?.Score,
            UpName = upName,
            UpHeader = _bangumiSeason.UpInfo?.Avatar ?? string.Empty,
            UpperMid = _bangumiSeason.UpInfo?.Mid ?? -1
        };

        return videoInfoView;
    }
}
