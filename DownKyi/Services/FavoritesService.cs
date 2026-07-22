using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.ViewModels.PageViewModels;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;
using ApiFavoritesMediaResource = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMediaResource;

namespace DownKyi.Services;

internal sealed class FavoritesService : IFavoritesService
{
    internal const string UnavailableMediaTitle = "已失效视频";
    private readonly ISettingsStore _settingsStore;
    private readonly IAppNavigationService _navigationService;

    public FavoritesService(ISettingsStore settingsStore, IAppNavigationService navigationService)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
    }

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

    public ApiFavoritesMediaResource GetFavoritesMediaPage(
        long mediaId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken)
    {
        return FavoritesResource.GetFavoritesMediaResource(
            mediaId,
            page,
            pageSize,
            keyword,
            cancellationToken);
    }

    public IReadOnlyList<ApiFavoritesMedia> GetAllFavoritesMedia(
        long mediaId,
        CancellationToken cancellationToken)
    {
        return FavoritesResource.GetAllFavoritesMedia(mediaId, cancellationToken);
    }

    public IReadOnlyList<FavoritesMedia> MapFavoritesMedia(
        IReadOnlyList<ApiFavoritesMedia> medias,
        AppRoute parentRoute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(medias);

        var order = 0;
        var result = new List<FavoritesMedia>(medias.Count);
        foreach (var media in medias)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;
            var unavailable = IsUnavailable(media);
            result.Add(new FavoritesMedia(_navigationService, parentRoute, _settingsStore)
            {
                Avid = media.Id,
                Bvid = media.Bvid,
                Order = order,
                Cover = media.Cover,
                Title = unavailable && string.IsNullOrWhiteSpace(media.Title)
                    ? UnavailableMediaTitle
                    : media.Title,
                IsUnavailable = unavailable,
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

    internal static bool IsUnavailable(ApiFavoritesMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.Attr != 0 ||
               string.Equals(media.Title, UnavailableMediaTitle, StringComparison.Ordinal);
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
