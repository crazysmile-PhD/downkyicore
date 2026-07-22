using DownKyi.Core.BiliApi.Favorites;

namespace DownKyi.Core.Tests;

public sealed class FavoritesSearchContractTests
{
    [Fact]
    public void SearchUrlTrimsAndEscapesKeyword()
    {
        var url = FavoritesResource.BuildFavoritesMediaUrl(42, 2, 20, "  C# 教程  ");

        Assert.Equal(
            "https://api.bilibili.com/x/v3/fav/resource/list?media_id=42&pn=2&ps=20&platform=web&keyword=C%23%20%E6%95%99%E7%A8%8B",
            url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySearchDoesNotAddKeyword(string? keyword)
    {
        var url = FavoritesResource.BuildFavoritesMediaUrl(42, 1, 20, keyword);

        Assert.DoesNotContain("keyword=", url, StringComparison.Ordinal);
    }
}
