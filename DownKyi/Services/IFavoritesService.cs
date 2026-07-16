using System.Collections.Generic;
using System.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.PageViewModels;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;

namespace DownKyi.Services;

internal interface IFavoritesService
{
    FavoritesPageItem? GetFavorites(long mediaId, CancellationToken cancellationToken = default);

    IReadOnlyList<FavoritesMedia> MapFavoritesMedia(
        IReadOnlyList<ApiFavoritesMedia> medias,
        AppRoute parentRoute,
        CancellationToken cancellationToken);

    IReadOnlyList<TabHeader> GetCreatedFavorites(long mid, CancellationToken cancellationToken);

    IReadOnlyList<TabHeader> GetCollectedFavorites(long mid, CancellationToken cancellationToken);
}
