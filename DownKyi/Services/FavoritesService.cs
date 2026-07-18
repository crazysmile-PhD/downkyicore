using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Media.Imaging;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;
using FavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;

namespace DownKyi.Services;

internal class FavoritesService : IFavoritesService
{
    private const string UnavailableMediaTitle = "已失效视频";

    /// <summary>
    /// 获取收藏夹元数据
    /// </summary>
    /// <param name="mediaId"></param>
    /// <returns></returns>
    public FavoritesPageItem? GetFavorites(long mediaId, CancellationToken cancellationToken = default)
    {
        var favoritesMetaInfo = FavoritesInfo.GetFavoritesInfo(mediaId, cancellationToken);
        if (favoritesMetaInfo == null)
        {
            return null;
        }

        // 查询、保存封面
        var coverUrl = favoritesMetaInfo.Cover ?? string.Empty;

        // 获取用户头像
        var upName = favoritesMetaInfo.Upper?.Name ?? string.Empty;

        // 为Favorites赋值
        var favorites = new FavoritesPageItem();
        App.PropertyChangeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            favorites.CoverUrl = coverUrl;

            favorites.Title = favoritesMetaInfo.Title;

            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
            var dateTime = startTime.AddSeconds(favoritesMetaInfo.Ctime);
            favorites.CreateTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

            favorites.PlayNumber = Format.FormatNumber(favoritesMetaInfo.CntInfo.Play);
            favorites.LikeNumber = Format.FormatNumber(favoritesMetaInfo.CntInfo.ThumbUp);
            favorites.FavoriteNumber = Format.FormatNumber(favoritesMetaInfo.CntInfo.Collect);
            favorites.ShareNumber = Format.FormatNumber(favoritesMetaInfo.CntInfo.Share);
            favorites.Description = favoritesMetaInfo.Intro;
            favorites.MediaCount = favoritesMetaInfo.MediaCount;

            favorites.UpName = upName;
            favorites.UpHeader = favoritesMetaInfo.Upper?.Face ?? string.Empty;
            favorites.UpperMid = favoritesMetaInfo.Upper?.Mid ?? -1;
        });

        return favorites;
    }

    ///// <summary>
    ///// 获取收藏夹所有内容明细列表
    ///// </summary>
    ///// <param name="mediaId"></param>
    ///// <param name="result"></param>
    ///// <param name="eventAggregator"></param>
    //public void GetFavoritesMediaList(long mediaId, ObservableCollection<FavoritesMedia> result, IEventAggregator eventAggregator, CancellationToken cancellationToken)
    //{
    //    List<Core.BiliApi.Favorites.Models.FavoritesMedia> medias = FavoritesResource.GetAllFavoritesMedia(mediaId);
    //    if (medias.Count == 0) { return; }

    //    GetFavoritesMediaList(medias, result, eventAggregator, cancellationToken);
    //}

    ///// <summary>
    ///// 获取收藏夹指定页的内容明细列表
    ///// </summary>
    ///// <param name="mediaId"></param>
    ///// <param name="pn"></param>
    ///// <param name="ps"></param>
    ///// <param name="result"></param>
    ///// <param name="eventAggregator"></param>
    //public void GetFavoritesMediaList(long mediaId, int pn, int ps, ObservableCollection<FavoritesMedia> result, IEventAggregator eventAggregator, CancellationToken cancellationToken)
    //{
    //    List<Core.BiliApi.Favorites.Models.FavoritesMedia> medias = FavoritesResource.GetFavoritesMedia(mediaId, pn, ps);
    //    if (medias.Count == 0) { return; }

    //    GetFavoritesMediaList(medias, result, eventAggregator, cancellationToken);
    //}

    /// <summary>
    /// 获取收藏夹内容明细列表
    /// </summary>
    /// <param name="medias"></param>
    /// <param name="result"></param>
    /// <param name="eventAggregator"></param>
    /// <param name="cancellationToken"></param>
    public void GetFavoritesMediaList(IReadOnlyList<FavoritesMedia> medias, ObservableCollection<ViewModels.PageViewModels.FavoritesMedia> result, IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(medias);

        var order = 0;
        var mappedMedias = new List<ViewModels.PageViewModels.FavoritesMedia>();
        foreach (var media in medias)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;
            mappedMedias.Add(MapFavoritesMedia(media, eventAggregator, order));
        }

        App.PropertyChangeAsync(() =>
        {
            var existingAvids = result.Select(item => item.Avid).ToHashSet();
            var newItems = mappedMedias.Where(item => existingAvids.Add(item.Avid)).ToList();
            if (result is RangeObservableCollection<ViewModels.PageViewModels.FavoritesMedia> range)
            {
                range.AddRange(newItems);
                return;
            }

            foreach (var item in newItems)
            {
                result.Add(item);
            }
        });
    }

    internal static ViewModels.PageViewModels.FavoritesMedia MapFavoritesMedia(
        FavoritesMedia media,
        IEventAggregator eventAggregator,
        int order)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(eventAggregator);

        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
        var createTime = startTime.AddSeconds(media.Ctime).ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        var favoriteTime = startTime.AddSeconds(media.FavTime).ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

        var isUnavailable = IsUnavailable(media);
        var title = isUnavailable && string.IsNullOrWhiteSpace(media.Title)
            ? UnavailableMediaTitle
            : media.Title;

        return new ViewModels.PageViewModels.FavoritesMedia(eventAggregator)
        {
            Avid = media.Id,
            Bvid = media.Bvid,
            Order = order,
            Cover = media.Cover,
            Title = title,
            IsUnavailable = isUnavailable,
            PlayNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Play) : "0",
            DanmakuNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Danmaku) : "0",
            FavoriteNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Collect) : "0",
            Duration = Format.FormatDuration2(media.Duration),
            UpName = media.Upper != null ? media.Upper.Name : string.Empty,
            UpMid = media.Upper != null ? media.Upper.Mid : -1,
            CreateTime = createTime,
            FavTime = favoriteTime
        };
    }

    internal static bool IsUnavailable(FavoritesMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.Attr != 0 || string.Equals(media.Title, UnavailableMediaTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// 更新我创建的收藏夹列表
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="tabHeaders"></param>
    /// <param name="cancellationToken"></param>
    public void GetCreatedFavorites(long mid, ObservableCollection<TabHeader> tabHeaders, CancellationToken cancellationToken)
    {
        var favorites = FavoritesInfo.GetAllCreatedFavorites(mid, cancellationToken);
        if (favorites.Count == 0)
        {
            return;
        }

        var headers = new List<TabHeader>();
        foreach (var item in favorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            headers.Add(new TabHeader { Id = item.Id, Title = item.Title, SubTitle = item.MediaCount.ToString(CultureInfo.CurrentCulture) });
        }

        App.PropertyChangeAsync(() =>
        {
            if (tabHeaders is RangeObservableCollection<TabHeader> range)
            {
                range.AddRange(headers);
                return;
            }

            foreach (var header in headers)
            {
                tabHeaders.Add(header);
            }
        });
    }

    /// <summary>
    /// 更新我收藏的收藏夹列表
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="tabHeaders"></param>
    /// <param name="cancellationToken"></param>
    public void GetCollectedFavorites(long mid, ObservableCollection<TabHeader> tabHeaders, CancellationToken cancellationToken)
    {
        var favorites = FavoritesInfo.GetAllCollectedFavorites(mid, cancellationToken);
        if (favorites.Count == 0)
        {
            return;
        }

        var headers = new List<TabHeader>();
        foreach (var item in favorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            headers.Add(new TabHeader { Id = item.Id, Title = item.Title, SubTitle = item.MediaCount.ToString(CultureInfo.CurrentCulture) });
        }

        App.PropertyChangeAsync(() =>
        {
            if (tabHeaders is RangeObservableCollection<TabHeader> range)
            {
                range.AddRange(headers);
                return;
            }

            foreach (var header in headers)
            {
                tabHeaders.Add(header);
            }
        });
    }
}
