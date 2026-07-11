using DownKyi.Core.BiliApi.Bangumi.Models;
using DownKyi.Core.BiliApi.VideoStream;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class BiliApiModelContractTests
{
    private static readonly string[] ExpectedStyles = { "sci-fi", "adventure" };

    [Fact]
    public void BangumiStyles_DeserializesJsonArray()
    {
        var season = JsonConvert.DeserializeObject<BangumiSeason>("""
            { "styles": ["sci-fi", "adventure"] }
            """);

        Assert.NotNull(season);
        Assert.Equal(ExpectedStyles, season.Styles);
    }

    [Fact]
    public void VideoPlayPageUrl_PrefersBvid()
    {
        var url = VideoStream.BuildVideoPlayPageUrl(170001, "BV17x411w7KC", 2);

        Assert.Equal("https://www.bilibili.com/video/BV17x411w7KC/?p=2", url);
    }

    [Fact]
    public void VideoPlayPageUrl_FallsBackToAvid()
    {
        var url = VideoStream.BuildVideoPlayPageUrl(170001, string.Empty, 3);

        Assert.Equal("https://www.bilibili.com/video/av170001/?p=3", url);
    }
}
