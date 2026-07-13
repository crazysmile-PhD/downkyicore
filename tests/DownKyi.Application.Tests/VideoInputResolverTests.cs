using DownKyi.Application.Media;

namespace DownKyi.Application.Tests;

public sealed class VideoInputResolverTests
{
    [Theory]
    [InlineData("BV17x411w7KC", VideoInputKind.Video)]
    [InlineData("av170001", VideoInputKind.Video)]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC", VideoInputKind.Video)]
    [InlineData("ss32982", VideoInputKind.Bangumi)]
    [InlineData("ep317925", VideoInputKind.Bangumi)]
    [InlineData("md28228367", VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/bangumi/play/ss32982", VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489", VideoInputKind.Cheese)]
    [InlineData("not-a-video", VideoInputKind.Unknown)]
    public void ResolveReturnsExpectedInputKind(string input, VideoInputKind expectedKind)
    {
        Assert.Equal(expectedKind, VideoInputResolver.Resolve(input));
    }
}
