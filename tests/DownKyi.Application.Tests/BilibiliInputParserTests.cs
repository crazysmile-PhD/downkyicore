using DownKyi.Application.Media;

namespace DownKyi.Application.Tests;

public sealed class BilibiliInputParserTests
{
    [Theory]
    [InlineData("BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("av170001", typeof(BilibiliAvidInput))]
    [InlineData("SS32982", typeof(BilibiliBangumiSeasonInput))]
    [InlineData("ep317925", typeof(BilibiliBangumiEpisodeInput))]
    [InlineData("md28228367", typeof(BilibiliBangumiMediaInput))]
    [InlineData("ml1329019876", typeof(BilibiliFavoritesInput))]
    [InlineData("uid928123", typeof(BilibiliUserSpaceInput))]
    [InlineData("UID:928123", typeof(BilibiliUserSpaceInput))]
    public void ParseRecognizesSupportedBareIdentifiers(string input, Type expectedType)
    {
        Assert.Equal(expectedType, BilibiliInputParser.Parse(input)?.GetType());
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("http://m.bilibili.com/video/av170001?p=2#reply", typeof(BilibiliAvidInput))]
    [InlineData("https://BILIBILI.COM:443/video/BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("https://www.bilibili.com/s/video/BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("https://www.bilibili.com/zh-Hans/video/BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("https://www.bilibili.com/zh-Hans/video/BV1smxM6YEXN/?spm_id_from=333.788.recommend_more_video.0", typeof(BilibiliBvidInput))]
    [InlineData("https://b23.tv/BV17x411w7KC", typeof(BilibiliBvidInput))]
    [InlineData("https://www.bilibili.com/bangumi/play/ss32982", typeof(BilibiliBangumiSeasonInput))]
    [InlineData("https://www.bilibili.com/bangumi/play/ep317925", typeof(BilibiliBangumiEpisodeInput))]
    [InlineData("https://www.bilibili.com/bangumi/media/md28228367", typeof(BilibiliBangumiMediaInput))]
    [InlineData("https://www.bilibili.com/cheese/play/ss205", typeof(BilibiliCheeseSeasonInput))]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489", typeof(BilibiliCheeseEpisodeInput))]
    [InlineData("https://www.bilibili.com/medialist/detail/ml1329019876", typeof(BilibiliFavoritesInput))]
    [InlineData("https://www.bilibili.com/medialist/play/ml1329019876/BV17x411w7KC", typeof(BilibiliFavoritesInput))]
    [InlineData("https://space.bilibili.com/928123/upload/video", typeof(BilibiliUserSpaceInput))]
    [InlineData("https://www.bilibili.com/list/3546801722362343", typeof(BilibiliPublicationInput))]
    [InlineData("https://www.bilibili.com/list/42?sid=99", typeof(BilibiliSeriesInput))]
    public void ParseRecognizesSupportedUrls(string input, Type expectedType)
    {
        Assert.Equal(expectedType, BilibiliInputParser.Parse(input)?.GetType());
    }

    [Fact]
    public void ParseReturnsStructuredValuesAndCanonicalAddress()
    {
        var series = Assert.IsType<BilibiliSeriesInput>(
            BilibiliInputParser.Parse("https://www.bilibili.com/list/42?sid=99"));

        Assert.Equal(42, series.Mid);
        Assert.Equal(99, series.SeriesId);
        Assert.Equal("https://www.bilibili.com/list/42?sid=99", series.CanonicalUri.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-video")]
    [InlineData("BV17x411w7K-")]
    [InlineData("bv17x411w7KC")]
    [InlineData("av0")]
    [InlineData("https://example.com/video/BV17x411w7KC")]
    [InlineData("https://www.bilibili.com.evil.test/video/BV17x411w7KC")]
    [InlineData("https://evil.test@www.bilibili.com/video/BV17x411w7KC")]
    [InlineData("https://www.bilibili.com:444/video/BV17x411w7KC")]
    [InlineData("ftp://www.bilibili.com/video/BV17x411w7KC")]
    [InlineData("https://www.bilibili.com/foo/video/BV17x411w7KC")]
    [InlineData("https://www.bilibili.com/video//BV17x411w7KC")]
    [InlineData("https://www.bilibili.com/video%2FBV17x411w7KC")]
    [InlineData("https://b23.tv/video/BV17x411w7KC")]
    [InlineData("https://space.bilibili.com/928123/fans")]
    [InlineData("https://www.bilibili.com/list/42?sid=99&sid=100")]
    [InlineData("https://www.bilibili.com/list/42?sid=invalid")]
    public void ParseRejectsUnsupportedOrDeceptiveInput(string? input)
    {
        Assert.Null(BilibiliInputParser.Parse(input));
    }
}
