using System.Net;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using Newtonsoft.Json;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class VideoStreamSubtitleDiagnosticsTests : IDisposable
{
    private readonly WebClientTestContext _context = new();
    private static readonly WbiKeys Keys = new(
        "7cd084941338484aae1ad9425b84077c",
        "4932caff0ff746eab6f01bf08b70ac45");

    [Fact]
    public void MalformedAiSubtitleIsSkippedAndReportedToTheCaller()
    {
        var call = 0;
        BiliWebClient.SendOverrideForTests = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(call++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"//example.test/subtitle.json","type":1}]}}}
                  """
                : "{not-json")
        };
        Exception? reported = null;

        var result = VideoStreamApi.GetSubtitle(
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

    public void Dispose()
    {
        _context.Dispose();
    }
}
