using DownKyi.Application.Desktop;
using DownKyi.Services;
using DownKyi.ViewModels.PageViewModels;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;

namespace DownKyi.Tests;

public sealed class FavoritesCoordinatorTests
{
    [Fact]
    public async Task PreCanceledFolderLoadDoesNotCallFavoritesApi()
    {
        var coordinator = new FavoritesCoordinator(new ThrowingFavoritesService());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadFoldersAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledPublicLoadDoesNotCallFavoritesApi()
    {
        var coordinator = new FavoritesCoordinator(new ThrowingFavoritesService());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadPublicFavoritesAsync(42, cancellation.Token));
    }

    private sealed class ThrowingFavoritesService : IFavoritesService
    {
        public FavoritesPageItem? GetFavorites(long mediaId, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The API should not be called for a canceled request.");
        }

        public IReadOnlyList<FavoritesMedia> MapFavoritesMedia(
            IReadOnlyList<ApiFavoritesMedia> medias,
            AppRoute parentRoute,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Mapping should not run for a canceled request.");
        }

        public IReadOnlyList<TabHeader> GetCreatedFavorites(long mid, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The API should not be called for a canceled request.");
        }

        public IReadOnlyList<TabHeader> GetCollectedFavorites(long mid, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The API should not be called for a canceled request.");
        }
    }
}
