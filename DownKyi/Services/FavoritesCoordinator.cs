using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;

namespace DownKyi.Services;

internal sealed record PublicFavoritesSnapshot(
    FavoritesPageItem Favorites,
    IReadOnlyList<FavoritesMedia> Medias);

internal interface IFavoritesCoordinator
{
    Task<IReadOnlyList<TabHeader>> LoadFoldersAsync(long mid, CancellationToken cancellationToken);

    Task<IReadOnlyList<FavoritesMedia>> LoadMediaPageAsync(
        long favoritesId,
        int page,
        int pageSize,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken);

    Task<PublicFavoritesSnapshot?> LoadPublicFavoritesAsync(
        long favoritesId,
        IEventAggregator eventAggregator,
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

    public Task<IReadOnlyList<FavoritesMedia>> LoadMediaPageAsync(
        long favoritesId,
        int page,
        int pageSize,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        return Task.Run<IReadOnlyList<FavoritesMedia>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var medias = FavoritesResource.GetFavoritesMedia(
                favoritesId,
                page,
                pageSize,
                cancellationToken);
            return medias == null || medias.Count == 0
                ? Array.Empty<FavoritesMedia>()
                : _favoritesService.MapFavoritesMedia(medias, eventAggregator, cancellationToken);
        }, cancellationToken);
    }

    public Task<PublicFavoritesSnapshot?> LoadPublicFavoritesAsync(
        long favoritesId,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var favorites = _favoritesService.GetFavorites(favoritesId, cancellationToken);
            if (favorites == null)
            {
                return null;
            }

            var medias = FavoritesResource.GetAllFavoritesMedia(favoritesId, cancellationToken);
            var mapped = _favoritesService.MapFavoritesMedia(medias, eventAggregator, cancellationToken);
            return new PublicFavoritesSnapshot(favorites, mapped);
        }, cancellationToken);
    }
}
