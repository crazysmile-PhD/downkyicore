using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Services.UserSpace;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class UserSpaceFavoritesTests
{
    [Fact]
    public async Task LoadingSpaceDoesNotRequestLegacySettingsForTheHeaderImage()
    {
        const long mid = 3_493_080_065_903_512;
        const string topPhoto = "http://i0.hdslb.com/bfs/space/header.png";
        var requests = new List<string>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(request.RequestAddress);
                var response = request.RequestAddress switch
                {
                    var address when address.Contains("/x/space/wbi/acc/info?", StringComparison.Ordinal) =>
                        $$$"""{"code":0,"data":{"mid":{{{mid}}},"name":"fixture","top_photo":"{{{topPhoto}}}"}}""",
                    var address when address.Contains("/x/space/wbi/arc/search?", StringComparison.Ordinal) =>
                        """{"code":0,"data":{"list":{"tlist":{}}}}""",
                    var address when address.Contains("/x/polymer/web-space/seasons_series_list?", StringComparison.Ordinal) =>
                        """{"code":0,"data":{"items_lists":{"page":{"total":0}}}}""",
                    var address when address.Contains("/x/v3/fav/folder/created/list?", StringComparison.Ordinal) =>
                        """{"code":0,"data":{"count":0,"list":[]}}""",
                    var address when address.Contains("/x/relation/stat?", StringComparison.Ordinal) =>
                        $$$"""{"code":0,"data":{"mid":{{{mid}}},"following":0,"black":0,"follower":0}}""",
                    var address when address.Contains("/x/space/upstat?", StringComparison.Ordinal) =>
                        """{"code":0,"data":{"archive":{"view":0},"article":{"view":0},"likes":0}}""",
                    _ => throw new InvalidOperationException(
                        $"Unexpected Bilibili request: {request.RequestAddress}")
                };
                return Task.FromResult(response);
            }
        };
        var coordinator = new UserSpaceLoadCoordinator(
            new TestWbiKeyProvider(),
            NullLogger<UserSpaceLoadCoordinator>.Instance,
            client);

        var snapshot = await coordinator.LoadAsync(mid, CancellationToken.None);

        Assert.Equal(topPhoto, Assert.IsType<DownKyi.Core.BiliApi.Users.Models.UserInfoForSpace>(snapshot.User).TopPhoto);
        Assert.Equal("https://i0.hdslb.com/bfs/space/header.png", snapshot.Background);
        Assert.DoesNotContain(
            requests,
            address => address.Contains("/ajax/settings/getSettings", StringComparison.Ordinal));
    }

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
        Assert.Equal("avares://DownKyi.Desktop/Resources/video-placeholder.png", folder.Cover);
    }

    [Fact]
    public void SelectingFolderUsesTypedPublicFavoritesRoute()
    {
        var navigation = new TestNavigationService();
        using var viewModel = new FavoritesViewModel(new TestDesktopInteractionContext(navigation));
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
