using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class VideoInputResolverTests
{
    [Theory]
    [InlineData("BV17x411w7KC", (int)VideoInputKind.Video)]
    [InlineData("av170001", (int)VideoInputKind.Video)]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC", (int)VideoInputKind.Video)]
    [InlineData("ss32982", (int)VideoInputKind.Bangumi)]
    [InlineData("ep317925", (int)VideoInputKind.Bangumi)]
    [InlineData("md28228367", (int)VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/bangumi/play/ss32982", (int)VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489", (int)VideoInputKind.Cheese)]
    [InlineData("not-a-video", (int)VideoInputKind.Unknown)]
    public void Resolve_ReturnsExpectedInputKind(string input, int expectedKind)
    {
        Assert.Equal((VideoInputKind)expectedKind, VideoInputResolver.Resolve(input));
    }

    [Theory]
    [InlineData("BV17x411w7KC", PlayStreamType.Video)]
    [InlineData("ss32982", PlayStreamType.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ss205", PlayStreamType.Cheese)]
    public void ResolvePlayStreamType_ReturnsExpectedDownloadStreamType(string input, PlayStreamType expectedStreamType)
    {
        Assert.Equal(expectedStreamType, VideoInputResolver.ResolvePlayStreamType(input));
    }

    [Fact]
    public void ResolvePlayStreamType_ReturnsNull_ForUnsupportedInput()
    {
        Assert.Null(VideoInputResolver.ResolvePlayStreamType("ml1329019876"));
    }
}
