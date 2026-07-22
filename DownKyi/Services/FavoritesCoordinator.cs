using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services;

internal sealed record PublicFavoritesSnapshot(
    FavoritesPageItem Favorites,
    IReadOnlyList<FavoritesMedia> Medias);

internal sealed record FavoritesMediaPageSnapshot(
    IReadOnlyList<FavoritesMedia> Medias,
    bool HasMore);

internal interface IFavoritesCoordinator
{
    Task<IReadOnlyList<TabHeader>> LoadFoldersAsync(long mid, CancellationToken cancellationToken);

    Task<FavoritesMediaPageSnapshot> LoadMediaPageAsync(
        long favoritesId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken);

    Task<PublicFavoritesSnapshot?> LoadPublicFavoritesAsync(
        long favoritesId,
        CancellationToken cancellationToken);
}

internal sealed class FavoritesCoordinator : IFavoritesCoordinator
{
    private readonly IFavoritesService _favoritesService;

    public FavoritesCoordinator(IFavoritesService favoritesService)
    {
        _favoritesService = favoritesService ?? throw new ArgumentNullException(nameof(favoritesService));
    }

    public Task<IReadOnlyList<TabHeader>> LoadFoldersAsync(long mid, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<TabHeader>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = _favoritesService.GetCreatedFavorites(mid, cancellationToken);
            var collected = _favoritesService.GetCollectedFavorites(mid, cancellationToken);
            var result = new List<TabHeader>(created.Count + collected.Count);
            result.AddRange(created);
            result.AddRange(collected);
            return result;
        }, cancellationToken);
    }

    public Task<FavoritesMediaPageSnapshot> LoadMediaPageAsync(
        long favoritesId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = _favoritesService.GetFavoritesMediaPage(
                favoritesId,
                page,
                pageSize,
                keyword,
                cancellationToken);
            var mapped = resource.Medias.Count == 0
                ? Array.Empty<FavoritesMedia>()
                : _favoritesService.MapFavoritesMedia(resource.Medias, AppRoute.MyFavorites, cancellationToken);
            return new FavoritesMediaPageSnapshot(mapped, resource.HasMore);
        }, cancellationToken);
    }

    public Task<PublicFavoritesSnapshot?> LoadPublicFavoritesAsync(
        long favoritesId,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var favorites = _favoritesService.GetFavorites(favoritesId, cancellationToken);
            if (favorites == null)
            {
                return null;
            }

            var medias = _favoritesService.GetAllFavoritesMedia(favoritesId, cancellationToken);
            var mapped = _favoritesService.MapFavoritesMedia(medias, AppRoute.PublicFavorites, cancellationToken);
            return new PublicFavoritesSnapshot(favorites, mapped);
        }, cancellationToken);
    }
}
