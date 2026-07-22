using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Services.UserSpace;
using DownKyi.ViewModels.UserSpace;

namespace DownKyi.Tests;

public sealed class UserSpaceFavoritesTests
{
    [Fact]
    public void FolderProjectionFiltersEmptyFoldersAndSuppliesPlaceholderCover()
    {
        var folders = UserSpaceLoadCoordinator.MapFavoriteFolders(
        [
            new FavoritesMetaInfo
            {
                Id = 1,
                Title = "visible",
                MediaCount = 3,
                Mtime = 1_700_000_000
            },
            new FavoritesMetaInfo
            {
                Id = 2,
                Title = "empty",
                MediaCount = 0
            }
        ]);

        var folder = Assert.Single(folders);
        Assert.Equal(1, folder.Id);
        Assert.Equal(3, folder.MediaCount);
        Assert.Equal("avares://DownKyi/Resources/video-placeholder.png", folder.Cover);
    }

    [Fact]
    public void SelectingFolderUsesTypedPublicFavoritesRoute()
    {
        var navigation = new TestNavigationService();
        using var viewModel = new ViewFavoritesViewModel(new TestDesktopInteractionContext(navigation));
        var folder = new UserSpaceFavoriteFolder(
            42,
            "cover",
            "folder",
            5,
            1_700_000_000);
        viewModel.OnNavigatedTo(new AppNavigationContext(
            AppNavigationRegion.UserSpace,
            AppRoute.UserSpaceFavorites,
            AppRoute.UserSpace,
            null,
            new AppNavigationParameters(new Dictionary<string, object?>
            {
                ["object"] = new[] { folder }
            })));

        viewModel.FavoritesCommand.Execute(Assert.Single(viewModel.Favorites));

        Assert.Equal(
            new AppNavigationRequest(AppRoute.PublicFavorites, AppRoute.UserSpace, 42L),
            Assert.Single(navigation.Requests));
        Assert.Equal(-1, viewModel.SelectedItem);
    }
}
