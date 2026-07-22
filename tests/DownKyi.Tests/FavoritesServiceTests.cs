using DownKyi.Application.Desktop;
using DownKyi.Services;
using DownKyi.ViewModels;
using ApiFavoritesMedia = DownKyi.Core.BiliApi.Favorites.Models.FavoritesMedia;

namespace DownKyi.Tests;

public sealed class FavoritesServiceTests
{
    [Fact]
    public void UnavailableMediaKeepsMetadataAndCannotOpenOrBeSelected()
    {
        using var settings = new TestSettingsStore();
        var navigation = new TestNavigationService();
        var service = new FavoritesService(settings.Store, navigation);
        var source = new ApiFavoritesMedia
        {
            Id = 170001,
            Bvid = "BV17x411w7KC",
            Title = "原始标题（接口可用时）",
            Cover = "https://example.invalid/cover.jpg",
            Attr = 9,
            FavTime = 1_700_000_000
        };

        var mapped = Assert.Single(service.MapFavoritesMedia(
            [source],
            AppRoute.PublicFavorites,
            CancellationToken.None));
        mapped.IsSelected = true;
        mapped.TitleCommand.Execute(new object());

        Assert.True(mapped.IsUnavailable);
        Assert.False(mapped.IsSelected);
        Assert.Equal(source.Title, mapped.Title);
        Assert.Equal(source.Cover, mapped.Cover);
        Assert.Empty(navigation.Requests);
    }

    [Fact]
    public void UnavailableMediaWithoutTitleGetsStatusFallback()
    {
        using var settings = new TestSettingsStore();
        var service = new FavoritesService(settings.Store, new TestNavigationService());

        var mapped = Assert.Single(service.MapFavoritesMedia(
            [new ApiFavoritesMedia { Attr = 1 }],
            AppRoute.MyFavorites,
            CancellationToken.None));

        Assert.Equal(FavoritesService.UnavailableMediaTitle, mapped.Title);
    }

    [Fact]
    public void SelectionAndDownloadsExcludeUnavailableMedia()
    {
        using var settings = new TestSettingsStore();
        var available = new ViewModels.PageViewModels.FavoritesMedia(
            new TestNavigationService(),
            AppRoute.MyFavorites,
            settings.Store)
        {
            Bvid = "BV-available"
        };
        var unavailable = new ViewModels.PageViewModels.FavoritesMedia(
            new TestNavigationService(),
            AppRoute.MyFavorites,
            settings.Store)
        {
            Bvid = "BV-unavailable",
            IsUnavailable = true
        };

        FavoritesSelectionPolicy.SetAllAvailableSelected([available, unavailable], selected: true);
        var download = Assert.Single(FavoritesSelectionPolicy.CreateDownloadItems([available, unavailable]));

        Assert.True(available.IsSelected);
        Assert.False(unavailable.IsSelected);
        Assert.True(FavoritesSelectionPolicy.AreAllAvailableSelected([available, unavailable]));
        Assert.Equal("BV-available", download.Source);
    }
}
