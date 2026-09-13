using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class VideoStreamSubtitleDiagnosticsTests
{
    private static readonly WbiKeys Keys = new(
        "7cd084941338484aae1ad9425b84077c",
        "4932caff0ff746eab6f01bf08b70ac45");

    [Fact]
    public async Task MalformedAiSubtitleIsSkippedAndReportedToTheCaller()
    {
        var call = 0;
        var client = new StubBilibiliApiClient((_, _) => Task.FromResult(call++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"//example.test/subtitle.json","type":1}]}}}
                  """
                : "{not-json"));
        Exception? reported = null;

        var result = await client.GetSubtitleAsync(
            Keys,
            1702204169,
            1,
            "BV1xx411c7mD",
            2,
            exception => reported = exception,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.IsType<JsonReaderException>(reported);
        Assert.Equal(2, call);
    }

    [Fact]
    public async Task ProtocolRelativeSubtitleAddressIsRequestedOverHttps()
    {
        var call = 0;
        string? subtitleRequestAddress = null;
        var client = new StubBilibiliApiClient((request, _) =>
        {
            call++;
            if (call == 1)
            {
                return Task.FromResult(
                    """
                    {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"//aisubtitle.hdslb.com/path/%7Eexample.json?token=%2Fabc","type":1}]}}}
                    """);
            }

            subtitleRequestAddress = request.RequestAddress;
            return Task.FromResult(
                """
                {"body":[{"from":0,"to":1,"content":"subtitle"}]}
                """);
        });

        var result = await client.GetSubtitleAsync(
            Keys,
            1702204169,
            1,
            "BV1xx411c7mD",
            2,
            TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(2, call);
        Assert.Equal(
            "https://aisubtitle.hdslb.com/path/%7Eexample.json?token=%2Fabc",
            subtitleRequestAddress);
    }

    [Fact]
    public async Task SubtitleRequestDoesNotForwardPlayerCredentials()
    {
        var call = 0;
        BilibiliHttpRequest? playerRequest = null;
        BilibiliHttpRequest? subtitleRequest = null;
        var client = new StubBilibiliApiClient((request, _) =>
        {
            call++;
            if (call == 1)
            {
                playerRequest = request;
                return Task.FromResult(
                    """
                    {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"https://aisubtitle.hdslb.com/subtitle.json","type":1}]}}}
                    """);
            }

            subtitleRequest = request;
            return Task.FromResult(
                """
                {"body":[{"from":0,"to":1,"content":"subtitle"}]}
                """);
        });

        var result = await client.GetSubtitleAsync(
            Keys,
            1702204169,
            1,
            "BV1xx411c7mD",
            2,
            TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(2, call);
        Assert.NotNull(playerRequest);
        Assert.True(playerRequest.IncludeCredentials);
        Assert.True(playerRequest.IncludeBuvid);
        Assert.NotNull(subtitleRequest);
        Assert.False(subtitleRequest.IncludeCredentials);
        Assert.False(subtitleRequest.IncludeBuvid);
    }

    [Theory]
    [InlineData("file://aisubtitle.hdslb.com/bfs/ai_subtitle/example.json")]
    [InlineData("ftp://aisubtitle.hdslb.com/bfs/ai_subtitle/example.json")]
    [InlineData("data:application/json,subtitle")]
    public async Task UnsupportedSubtitleAddressSchemeIsSkipped(string subtitleAddress)
    {
        var call = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            call++;
            if (call != 1)
            {
                throw new InvalidOperationException("An unsupported subtitle address was requested.");
            }

            var playerResponse =
                """
                {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"__SUBTITLE_ADDRESS__","type":1}]}}}
                """;
            return Task.FromResult(playerResponse.Replace(
                "__SUBTITLE_ADDRESS__",
                subtitleAddress,
                StringComparison.Ordinal));
        });

        var result = await client.GetSubtitleAsync(
            Keys,
            1702204169,
            1,
            "BV1xx411c7mD",
            2,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(1, call);
    }

}
