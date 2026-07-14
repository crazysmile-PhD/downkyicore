using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;

namespace DownKyi.Services;

internal sealed class FavoritesService : IFavoritesService
{
    public FavoritesPageItem? GetFavorites(long mediaId, CancellationToken cancellationToken = default)
    {
        var metadata = FavoritesInfo.GetFavoritesInfo(mediaId, cancellationToken);
        if (metadata == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new FavoritesPageItem
        {
            CoverUrl = metadata.Cover ?? string.Empty,
            Title = metadata.Title,
            CreateTime = DateTimeOffset.FromUnixTimeSeconds(metadata.Ctime)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            PlayNumber = Format.FormatNumber(metadata.CntInfo.Play),
            LikeNumber = Format.FormatNumber(metadata.CntInfo.ThumbUp),
            FavoriteNumber = Format.FormatNumber(metadata.CntInfo.Collect),
            ShareNumber = Format.FormatNumber(metadata.CntInfo.Share),
            Description = metadata.Intro,
            MediaCount = metadata.MediaCount,
            UpName = metadata.Upper?.Name ?? string.Empty,
            UpHeader = metadata.Upper?.Face ?? string.Empty,
            UpperMid = metadata.Upper?.Mid ?? -1
        };
    }

    public IReadOnlyList<FavoritesMedia> MapFavoritesMedia(
        IReadOnlyList<ApiFavoritesMedia> medias,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(medias);
        ArgumentNullException.ThrowIfNull(eventAggregator);

        var order = 0;
        var result = new List<FavoritesMedia>(medias.Count);
        foreach (var media in medias)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(media.Title, "已失效视频", StringComparison.Ordinal))
            {
                continue;
            }

            order++;
            result.Add(new FavoritesMedia(eventAggregator)
            {
                Avid = media.Id,
                Bvid = media.Bvid,
                Order = order,
                Cover = media.Cover,
                Title = media.Title,
                PlayNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Play) : "0",
                DanmakuNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Danmaku) : "0",
                FavoriteNumber = media.CntInfo != null ? Format.FormatNumber(media.CntInfo.Collect) : "0",
                Duration = Format.FormatDuration2(media.Duration),
                UpName = media.Upper?.Name ?? string.Empty,
                UpMid = media.Upper?.Mid ?? -1,
                CreateTime = FormatDate(media.Ctime),
                FavTime = FormatDate(media.FavTime)
            });
        }

        return result;
    }

    public IReadOnlyList<TabHeader> GetCreatedFavorites(long mid, CancellationToken cancellationToken)
    {
        var favorites = FavoritesInfo.GetAllCreatedFavorites(mid, cancellationToken);
        var result = new List<TabHeader>(favorites.Count);
        foreach (var item in favorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new TabHeader
            {
                Id = item.Id,
                Title = item.Title,
                SubTitle = item.MediaCount.ToString(CultureInfo.CurrentCulture)
            });
        }

        return result;
    }

    public IReadOnlyList<TabHeader> GetCollectedFavorites(long mid, CancellationToken cancellationToken)
    {
        var favorites = FavoritesInfo.GetAllCollectedFavorites(mid, cancellationToken);
        var result = new List<TabHeader>(favorites.Count);
        foreach (var item in favorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new TabHeader
            {
                Id = item.Id,
                Title = item.Title,
                SubTitle = item.MediaCount.ToString(CultureInfo.CurrentCulture)
            });
        }

        return result;
    }

    private static string FormatDate(long timestamp)
    {
        return DateTimeOffset.FromUnixTimeSeconds(timestamp)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
}
