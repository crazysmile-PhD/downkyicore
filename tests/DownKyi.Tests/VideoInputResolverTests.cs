using DownKyi.Core.BiliApi.VideoStream;
using LegacyVideoInputResolver = DownKyi.Services.Video.VideoInputResolver;

namespace DownKyi.Tests;

public sealed class VideoInputResolverTests
{
    [Theory]
    [InlineData("BV17x411w7KC", PlayStreamType.Video)]
    [InlineData("ss32982", PlayStreamType.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ss205", PlayStreamType.Cheese)]
    public void ResolvePlayStreamTypeReturnsExpectedDownloadStreamType(string input, PlayStreamType expectedStreamType)
    {
        Assert.Equal(expectedStreamType, LegacyVideoInputResolver.ResolvePlayStreamType(input));
    }

    [Fact]
    public void ResolvePlayStreamTypeReturnsNullForUnsupportedInput()
    {
        Assert.Null(LegacyVideoInputResolver.ResolvePlayStreamType("ml1329019876"));
    }
}
