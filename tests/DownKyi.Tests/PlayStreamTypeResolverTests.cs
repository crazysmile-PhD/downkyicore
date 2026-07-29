using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class PlayStreamTypeResolverTests
{
    [Theory]
    [InlineData("BV17x411w7KC", PlayStreamType.Video)]
    [InlineData("ss32982", PlayStreamType.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ss205", PlayStreamType.Cheese)]
    public void ResolvePlayStreamTypeReturnsExpectedDownloadStreamType(string input, PlayStreamType expectedStreamType)
    {
        Assert.Equal(expectedStreamType, PlayStreamTypeResolver.ResolvePlayStreamType(input));
    }

    [Fact]
    public void ResolvePlayStreamTypeReturnsNullForUnsupportedInput()
    {
        Assert.Null(PlayStreamTypeResolver.ResolvePlayStreamType("ml1329019876"));
    }
}
