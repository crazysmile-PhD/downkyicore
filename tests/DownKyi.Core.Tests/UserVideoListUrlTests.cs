using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Core.Tests;

public sealed class UserVideoListUrlTests
{
    [Theory]
    [InlineData("https://www.bilibili.com/list/3546801722362343", 3546801722362343)]
    [InlineData("https://www.bilibili.com/list/42/", 42)]
    [InlineData("https://m.bilibili.com/list/42?spm_id_from=333.999.0.0", 42)]
    [InlineData("http://bilibili.com/list/42", 42)]
    public void BareNumericListUrlReturnsUploaderMid(string input, long expectedMid)
    {
        Assert.True(ParseEntrance.IsUserVideoListUrl(input));
        Assert.Equal(expectedMid, ParseEntrance.GetUserVideoListId(input));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/list/ml1329019876")]
    [InlineData("https://www.bilibili.com/list/42?sid=99")]
    [InlineData("https://www.bilibili.com/list/42/video")]
    [InlineData("https://evil.example/list/42")]
    [InlineData("https://www.bilibili.com/list/0")]
    [InlineData("https://www.bilibili.com/list/-1")]
    [InlineData("not a url")]
    public void NonUploaderListInputIsRejected(string input)
    {
        Assert.False(ParseEntrance.IsUserVideoListUrl(input));
        Assert.Equal(-1, ParseEntrance.GetUserVideoListId(input));
    }
}
