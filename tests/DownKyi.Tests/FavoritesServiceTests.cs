using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Services;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class FavoritesServiceTests
{
    [Fact]
    public void UnavailableMediaKeepsMetadataAndCannotBeSelected()
    {
        var source = new FavoritesMedia
        {
            Id = 170001,
            Bvid = "BV17x411w7KC",
            Title = "原始标题（接口可用时）",
            Cover = "https://example.invalid/cover.jpg",
            Attr = 9,
            FavTime = 1_700_000_000
        };

        var mapped = FavoritesService.MapFavoritesMedia(source, new EventAggregator(), 3);
        mapped.IsSelected = true;

        Assert.True(mapped.IsUnavailable);
        Assert.False(mapped.IsSelected);
        Assert.Equal(source.Title, mapped.Title);
        Assert.Equal(source.Cover, mapped.Cover);
        Assert.Equal(3, mapped.Order);
    }

    [Fact]
    public void MaskedUnavailableTitleIsRecognizedWithoutAttr()
    {
        var source = new FavoritesMedia { Title = "已失效视频" };

        Assert.True(FavoritesService.IsUnavailable(source));
    }

    [Fact]
    public void UnavailableMediaWithoutTitleGetsStatusFallback()
    {
        var source = new FavoritesMedia { Attr = 1 };

        var mapped = FavoritesService.MapFavoritesMedia(source, new EventAggregator(), 1);

        Assert.Equal("已失效视频", mapped.Title);
    }
}
