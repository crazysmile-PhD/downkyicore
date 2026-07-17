using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class PageMediaNavigationTests
{
    [Fact]
    public void FavoritesVideoUsesTypedVideoRouteAndParent()
    {
        using var settings = new TestSettingsStore();
        var navigation = new TestNavigationService();
        var media = new FavoritesMedia(navigation, AppRoute.MyFavorites, settings.Store)
        {
            Bvid = "BV1typed"
        };

        media.TitleCommand.Execute(new object());

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.VideoDetail, request.Route);
        Assert.Equal(AppRoute.MyFavorites, request.Parent);
        Assert.Equal("https://www.bilibili.com/video/BV1typed", request.Parameter);
    }

    [Fact]
    public void FavoritesUpperUsesMySpaceForCurrentUser()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            User = current.User with { Mid = 42 }
        });
        var navigation = new TestNavigationService();
        var media = new FavoritesMedia(navigation, AppRoute.PublicFavorites, settings.Store)
        {
            UpMid = 42
        };

        media.VideoUpperCommand.Execute(new object());

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.MySpace, request.Route);
        Assert.Equal(AppRoute.PublicFavorites, request.Parent);
        Assert.Equal(42L, request.Parameter);
    }

    [Fact]
    public void FriendUsesTypedUserSpaceRoute()
    {
        var navigation = new TestNavigationService();
        var friend = new FriendInfo(navigation, AppRoute.Friends) { Mid = 99 };

        friend.UserCommand.Execute(new object());

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Friends, 99L), request);
    }
}
