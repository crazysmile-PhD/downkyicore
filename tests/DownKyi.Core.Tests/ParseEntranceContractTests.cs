using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Core.Tests;

public sealed class ParseEntranceContractTests
{
    [Theory]
    [InlineData("av170001", 170001)]
    [InlineData("AV170001", 170001)]
    [InlineData("https://www.bilibili.com/video/av170001", 170001)]
    [InlineData("http://www.bilibili.com/video/av170001/", 170001)]
    [InlineData("https://www.bilibili.com/s/video/av170001?share_source=copy_web", 170001)]
    [InlineData("https://m.bilibili.com/video/av170001", 170001)]
    [InlineData("https://b23.tv/av170001", 170001)]
    public void AvInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetAvId(input));
    }

    [Theory]
    [InlineData("BV17x411w7KC", "BV17x411w7KC")]
    [InlineData("BV1U7V66FEiK", "BV1U7V66FEiK")]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC", "BV17x411w7KC")]
    [InlineData("http://www.bilibili.com/video/BV17x411w7KC/", "BV17x411w7KC")]
    [InlineData("https://www.bilibili.com/s/video/BV17x411w7KC?share_source=copy_web", "BV17x411w7KC")]
    [InlineData("https://m.bilibili.com/video/BV17x411w7KC", "BV17x411w7KC")]
    [InlineData("https://b23.tv/BV17x411w7KC", "BV17x411w7KC")]
    public void BvInputsRetainCanonicalContract(string input, string expected)
    {
        Assert.Equal(expected, ParseEntrance.GetBvId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-video")]
    [InlineData("bv17x411w7KC")]
    [InlineData("BV17x411w7K")]
    [InlineData("https://evil.example/video/BV17x411w7KC")]
    [InlineData("https://www.bilibili.com.evil/video/BV17x411w7KC")]
    public void InvalidVideoInputsRetainFailureSentinels(string input)
    {
        Assert.Equal(-1, ParseEntrance.GetAvId(input));
        Assert.Equal(string.Empty, ParseEntrance.GetBvId(input));
    }

    [Theory]
    [InlineData("ss32982", 32982)]
    [InlineData("SS32982", 32982)]
    [InlineData("https://www.bilibili.com/bangumi/play/ss32982", 32982)]
    [InlineData("https://b23.tv/ss32982", 32982)]
    public void BangumiSeasonInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetBangumiSeasonId(input));
    }

    [Theory]
    [InlineData("ep317925", 317925)]
    [InlineData("EP317925", 317925)]
    [InlineData("https://www.bilibili.com/bangumi/play/ep317925", 317925)]
    [InlineData("https://b23.tv/ep317925", 317925)]
    public void BangumiEpisodeInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetBangumiEpisodeId(input));
    }

    [Theory]
    [InlineData("md28228367", 28228367)]
    [InlineData("MD28228367", 28228367)]
    [InlineData("https://www.bilibili.com/bangumi/media/md28228367", 28228367)]
    public void BangumiMediaInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetBangumiMediaId(input));
    }

    [Fact]
    public void InvalidBangumiInputsRetainFailureSentinels()
    {
        const string input = "https://evil.example/bangumi/play/ss32982";

        Assert.Equal(-1, ParseEntrance.GetBangumiSeasonId(input));
        Assert.Equal(-1, ParseEntrance.GetBangumiEpisodeId(input));
        Assert.Equal(-1, ParseEntrance.GetBangumiMediaId(input));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/cheese/play/ss205", 205)]
    [InlineData("http://www.bilibili.com/cheese/play/ss205/", 205)]
    [InlineData("https://m.bilibili.com/cheese/play/ss205?from=search", 205)]
    public void CheeseSeasonUrlsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetCheeseSeasonId(input));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489", 3489)]
    [InlineData("https://m.bilibili.com/cheese/play/ep3489?from=search", 3489)]
    public void CheeseEpisodeUrlsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetCheeseEpisodeId(input));
    }

    [Fact]
    public void InvalidCheeseInputRetainsFailureSentinel()
    {
        const string input = "ss205";

        Assert.Equal(-1, ParseEntrance.GetCheeseSeasonId(input));
        Assert.Equal(-1, ParseEntrance.GetCheeseEpisodeId(input));
    }

    [Theory]
    [InlineData("ml1329019876", 1329019876)]
    [InlineData("ML1329019876", 1329019876)]
    [InlineData("https://www.bilibili.com/medialist/detail/ml1329019876", 1329019876)]
    [InlineData("https://www.bilibili.com/medialist/play/ml1329019876/BV17x411w7KC", 1329019876)]
    [InlineData("https://www.bilibili.com/list/ml1329019876", 1329019876)]
    public void FavoritesInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetFavoritesId(input));
    }

    [Fact]
    public void InvalidFavoritesInputRetainsFailureSentinel()
    {
        Assert.Equal(-1, ParseEntrance.GetFavoritesId(
            "https://www.bilibili.com.evil/list/ml1329019876"));
    }

    [Theory]
    [InlineData("uid928123", 928123)]
    [InlineData("UID928123", 928123)]
    [InlineData("uid:928123", 928123)]
    [InlineData("UID:928123", 928123)]
    [InlineData("https://space.bilibili.com/928123", 928123)]
    [InlineData("http://space.bilibili.com/928123/?spm_id_from=333.337.0.0", 928123)]
    public void UserInputsRetainCanonicalContract(string input, long expected)
    {
        Assert.Equal(expected, ParseEntrance.GetUserId(input));
    }

    [Theory]
    [InlineData("https://space.bilibili.com.evil/928123")]
    [InlineData("https://evil.example/space.bilibili.com/928123")]
    [InlineData("https://space.bilibili.com/user928123")]
    [InlineData("https://space.bilibili.com/928123/video")]
    [InlineData("https://space.bilibili.com/")]
    public void SpoofedOrMalformedUserSpaceUrlsAreRejected(string input)
    {
        Assert.False(ParseEntrance.IsUserUrl(input));
        Assert.Equal(-1, ParseEntrance.GetUserId(input));
    }

    [Fact]
    public void AllPublicStringParsersRejectNull()
    {
        var actions = new Action[]
        {
            () => ParseEntrance.IsAvId(null!),
            () => ParseEntrance.IsAvUrl(null!),
            () => ParseEntrance.GetAvId(null!),
            () => ParseEntrance.IsBvId(null!),
            () => ParseEntrance.IsBvUrl(null!),
            () => ParseEntrance.GetBvId(null!),
            () => ParseEntrance.IsBangumiSeasonId(null!),
            () => ParseEntrance.IsBangumiSeasonUrl(null!),
            () => ParseEntrance.GetBangumiSeasonId(null!),
            () => ParseEntrance.IsBangumiEpisodeId(null!),
            () => ParseEntrance.IsBangumiEpisodeUrl(null!),
            () => ParseEntrance.GetBangumiEpisodeId(null!),
            () => ParseEntrance.IsBangumiMediaId(null!),
            () => ParseEntrance.IsBangumiMediaUrl(null!),
            () => ParseEntrance.GetBangumiMediaId(null!),
            () => ParseEntrance.IsCheeseSeasonUrl(null!),
            () => ParseEntrance.GetCheeseSeasonId(null!),
            () => ParseEntrance.IsCheeseEpisodeUrl(null!),
            () => ParseEntrance.GetCheeseEpisodeId(null!),
            () => ParseEntrance.IsFavoritesId(null!),
            () => ParseEntrance.IsFavoritesUrl(null!),
            () => ParseEntrance.GetFavoritesId(null!),
            () => ParseEntrance.IsUserId(null!),
            () => ParseEntrance.IsUserUrl(null!),
            () => ParseEntrance.GetUserId(null!),
            () => ParseEntrance.IsUserVideoListUrl(null!),
            () => ParseEntrance.GetUserVideoListId(null!)
        };

        Assert.All(actions, action => Assert.Throws<ArgumentNullException>(action));
    }
}
