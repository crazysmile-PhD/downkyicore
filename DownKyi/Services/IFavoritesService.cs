using System.Collections.Generic;
using System.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.PageViewModels;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;
using ApiFavoritesMediaResource = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMediaResource;

namespace DownKyi.Services;

internal interface IFavoritesService
{
    FavoritesPageItem? GetFavorites(long mediaId, CancellationToken cancellationToken = default);

    ApiFavoritesMediaResource GetFavoritesMediaPage(
        long mediaId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken);

    IReadOnlyList<ApiFavoritesMedia> GetAllFavoritesMedia(
        long mediaId,
        CancellationToken cancellationToken);

    IReadOnlyList<FavoritesMedia> MapFavoritesMedia(
        IReadOnlyList<ApiFavoritesMedia> medias,
        AppRoute parentRoute,
        CancellationToken cancellationToken);

    IReadOnlyList<TabHeader> GetCreatedFavorites(long mid, CancellationToken cancellationToken);

    IReadOnlyList<TabHeader> GetCollectedFavorites(long mid, CancellationToken cancellationToken);
}
