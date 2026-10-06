using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class PlayUrlEnvelopeContractTests
{
    private const string ImgKey = "12345678901234567890123456789012";
    private const string SubKey = "abcdefghijklmnopqrstuvwxyzABCDEF";
    private static readonly WbiKeys Keys = new(ImgKey, SubKey);
    private static readonly string SampleDirectory = Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "DownKyi.Core.Tests",
        "BiliApi",
        "JsonSamples");

    [Fact]
    public void MissingEnvelopeFieldsRemainNull()
    {
        var response = ReadSample("playurl-missing-payload.json");

        Assert.Null(response.Data);
        Assert.Null(response.Result);
    }

    [Fact]
    public void DataOnlyResponseSelectsData()
    {
        var response = ReadSample("playurl-video-data.json");

        var payload = VideoStreamApi.SelectPlayUrlPayload(
            response,
            VideoStreamApi.PlayUrlPayloadField.Data,
            "video");

        Assert.Same(response.Data, payload);
        Assert.Null(response.Result);
        Assert.Equal(80, Assert.Single(payload.Dash.Video).Id);
    }

    [Fact]
    public void ResultOnlyResponseSelectsResultWithoutEmptyDataMaskingIt()
    {
        var response = ReadSample("playurl-bangumi-result.json");

        var payload = VideoStreamApi.SelectPlayUrlPayload(
            response,
            VideoStreamApi.PlayUrlPayloadField.Result,
            "bangumi");

        Assert.Null(response.Data);
        Assert.Same(response.Result, payload);
        Assert.Equal(1, Assert.Single(payload.Durl).Order);
    }

    [Fact]
    public void MissingExpectedEnvelopeThrowsTypedContractFailure()
    {
        var response = ReadSample("playurl-missing-payload.json");

        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            VideoStreamApi.SelectPlayUrlPayload(
                response,
                VideoStreamApi.PlayUrlPayloadField.Data,
                "video"));

        Assert.Equal("video", exception.Operation);
        Assert.Contains("no 'data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentButEmptyEnvelopeThrowsTypedContractFailure()
    {
        var response = ReadSample("playurl-empty-data.json");

        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            VideoStreamApi.SelectPlayUrlPayload(
                response,
                VideoStreamApi.PlayUrlPayloadField.Data,
                "video"));

        Assert.Contains("empty 'data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryVideoEndpointUsesDataEnvelope()
    {
        var client = CreateClient("playurl-video-data.json");

        var payload = await client.GetVideoPlayUrlAsync(
            Keys,
            1702204169,
            1,
            "BV1fixture",
            2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(80, Assert.Single(payload?.Dash.Video ?? []).Id);
    }

    [Fact]
    public async Task WebPageVideoEndpointFallsBackWhenDurlQualityDiffersFromRequestedQuality()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            if (request.RequestAddress.StartsWith(
                    "https://www.bilibili.com/video/",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    """
                    <script>window.__playinfo__={"code":0,"message":"success","data":{"quality":64,"video_codecid":7,"durl":[{"order":1,"url":"https://example.invalid/default"}]}}</script>
                    """);
            }

            return Task.FromResult(
                """
                {"code":0,"message":"success","data":{"quality":80,"video_codecid":7,"durl":[{"order":1,"url":"https://example.invalid/requested"}]}}
                """);
        });

        var payload = await client.GetVideoPlayUrlWebPageAsync(
            Keys,
            1702204169,
            1,
            "BV1fixture",
            2,
            1,
            quality: 80,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(80, payload?.Quality);
        Assert.Equal(2, requests.Count);
        Assert.Contains("qn=80", requests[1].RequestAddress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BangumiEndpointUsesResultVideoInfoEnvelope()
    {
        BilibiliHttpRequest? capturedRequest = null;
        var client = CreateClient(
            "playurl-bangumi-v2-result.json",
            request => capturedRequest = request);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(payload?.Durl ?? []).Order);
        var request = Assert.IsType<BilibiliHttpRequest>(capturedRequest);
        var requestUri = new Uri(request.RequestAddress);
        Assert.Equal("/pgc/player/web/v2/playurl", requestUri.AbsolutePath);
        Assert.Contains("cid=2", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("ep_id=3489", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("qn=127", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fourk=1", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnver=0", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnval=4048", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("bvid=BV1fixture", requestUri.Query, StringComparison.Ordinal);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ep3489", request.Referer);
        Assert.Equal(127, payload?.Diagnostics?.RequestedQuality);
        Assert.Equal("not-required", payload?.Diagnostics?.FallbackOutcome);
    }

    [Fact]
    public async Task BangumiEndpointUsesBetterEmbeddedPlaybackWhenApiLooksDegraded()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? DegradedBangumiApiResponse
                : BetterEmbeddedBangumiPage);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [112, 64],
            payload!.Dash.Video
                .Select(video => video.Id)
                .Distinct()
                .OrderByDescending(id => id)
                .ToArray());
        Assert.Equal(
            "https://api.invalid/video-64",
            payload.Dash.Video.Single(video => video.Id == 64).BaseAddress);
        Assert.Equal(
            "https://api.invalid/audio-30280",
            Assert.Single(payload.Dash.Audio).BaseAddress);
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ep3489", requests[1].RequestAddress);
        Assert.Equal(requests[1].RequestAddress, requests[1].Referer);
        Assert.True(payload?.Diagnostics?.UsedWebPageFallback);
        Assert.Equal("PLAY_WHOLE", payload?.Diagnostics?.PlayDetail);
        Assert.Equal("embedded-playback-merged", payload?.Diagnostics?.FallbackOutcome);
    }

    [Fact]
    public async Task BangumiEndpointDoesNotFetchWebPageForLegitimate720PResponse()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                """
                {"code":0,"result":{"video_info":{"quality":64,"accept_quality":[64,32,16],"support_formats":[{"quality":64}],"durl":[],"dash":{"video":[{"id":64,"codecid":7}],"audio":[{"id":30280}]}}}}
                """);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(64, Assert.Single(payload?.Dash.Video ?? []).Id);
        Assert.Single(requests);
        Assert.Equal("not-required", payload?.Diagnostics?.FallbackOutcome);
    }

    [Theory]
    [MemberData(nameof(EmbeddedBangumiPlaybackShapes))]
    public void EmbeddedBangumiPlaybackShapesNormalizeToVideoInfo(string webpage)
    {
        var parsed = BangumiPlaybackResolver.TryParseEmbeddedPayload(
            webpage,
            "embedded-test",
            out var payload,
            out var playDetail);

        Assert.True(parsed);
        Assert.Equal(112, Assert.Single(payload?.Dash.Video ?? []).Id);
        Assert.Equal("PLAY_WHOLE", playDetail);
    }

    [Fact]
    public void EmbeddedBangumiPlaybackRejectsNonSuccessEnvelope()
    {
        const string webpage =
            """
            <script>
            const playurlSSRData = {
              "code": -10403,
              "message": "restricted",
              "result": {
                "video_info": {
                  "durl": [],
                  "dash": {
                    "video": [{"id":112}],
                    "audio": [{"id":30280}]
                  }
                }
              }
            };
            </script>
            """;

        var parsed = BangumiPlaybackResolver.TryParseEmbeddedPayload(
            webpage,
            "embedded-test",
            out var payload,
            out _);

        Assert.False(parsed);
        Assert.Null(payload);
    }

    [Fact]
    public void EmbeddedBangumiPlaybackRejectsPlayVideoTypePreview()
    {
        const string webpage =
            """
            <script>
            const playurlSSRData = {
              "code": 0,
              "result": {
                "play_video_type": "preview",
                "video_info": {
                  "durl": [],
                  "dash": {
                    "video": [{"id":112}],
                    "audio": [{"id":30280}]
                  }
                }
              }
            };
            </script>
            """;

        var parsed = BangumiPlaybackResolver.TryParseEmbeddedPayload(
            webpage,
            "embedded-test",
            out var payload,
            out _);

        Assert.False(parsed);
        Assert.Null(payload);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task BangumiEndpointRejectsInvalidEpisodeIdBeforeRequest(long episodeId)
    {
        var requestCount = 0;
        var client = CreateClient(
            "playurl-bangumi-v2-result.json",
            _ => requestCount++);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                episodeId,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, requestCount);
    }

    [Fact]
    public void BangumiV2MissingVideoInfoThrowsTypedContractFailure()
    {
        var response = new BangumiPlayUrlV2Origin
        {
            Result = new BangumiPlayUrlV2Result()
        };

        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BangumiPlayUrlV2Contract.SelectPayload(response, "bangumi-v2"));

        Assert.Equal("bangumi-v2", exception.Operation);
        Assert.Contains("result.video_info", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedBangumiPlaybackPayloads))]
    public async Task BangumiV2NullPlaybackFieldThrowsTypedMalformedFailure(
        string fieldName,
        string responseBody)
    {
        var client = CreateClientFromBody(responseBody);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetBangumiPlayUrlAsync), exception.Operation);
        Assert.Contains("malformed playback payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains(fieldName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BangumiV2EmptyPlaybackCollectionsThrowTypedEmptyFailure()
    {
        var client = CreateClientFromBody(
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":[],"dash":{"video":[],"audio":[]}}}}
            """);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("empty 'result.video_info'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BangumiV2DashOnlyPayloadRemainsValid()
    {
        var client = CreateClientFromBody(
            """
            {"code":0,"message":"success","result":{"video_info":{"dash":{"video":[{"id":80}],"audio":[{"id":30280}]}}}}
            """);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(80, Assert.Single(payload?.Dash.Video ?? []).Id);
        Assert.Equal(30280, Assert.Single(payload?.Dash.Audio ?? []).Id);
    }

    [Fact]
    public async Task CheeseEndpointUsesDataEnvelope()
    {
        var client = CreateClient("playurl-cheese-data.json");

        var payload = await client.GetCheesePlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(30280, Assert.Single(payload?.Dash.Audio ?? []).Id);
    }

    [Fact]
    public async Task BangumiV2FullPlaybackRemainsValid()
    {
        var client = CreateClientFromBody(
            """
            {"code":0,"message":"success","result":{"video_info":{"is_preview":0,"durl":[{"order":1}],"dash":{"video":[],"audio":[]}}}}
            """);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(payload?.IsPreview);
        Assert.Single(payload?.Durl ?? []);
    }

    [Fact]
    public async Task BangumiV2PlayCheckPreviewIsRejected()
    {
        var client = CreateClient("playurl-bangumi-v2-preview.json");

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetBangumiPlayUrlAsync), exception.Operation);
        Assert.Contains("preview-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BangumiV2IsPreviewFlagIsRejected()
    {
        var client = CreateClientFromBody(
            """
            {"code":0,"message":"success","result":{"video_info":{"is_preview":1,"durl":[{"order":1}],"dash":{"video":[],"audio":[]}}}}
            """);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetBangumiPlayUrlAsync), exception.Operation);
        Assert.Contains("preview-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryVideoEndpointRejectsEmptyDataEnvelope()
    {
        var client = CreateClient("playurl-empty-data.json");

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetVideoPlayUrlAsync(
                Keys,
                1702204169,
                1,
                "BV1fixture",
                2,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetVideoPlayUrlAsync), exception.Operation);
    }

    private static PlayUrlOrigin ReadSample(string name)
    {
        return JsonConvert.DeserializeObject<PlayUrlOrigin>(
                   File.ReadAllText(Path.Combine(SampleDirectory, name)))
               ?? throw new InvalidDataException($"Sample '{name}' did not deserialize.");
    }

    public static TheoryData<string, string> MalformedBangumiPlaybackPayloads => new()
    {
        {
            "result.video_info.durl",
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":null,"dash":{"video":[{}],"audio":[{}]}}}}
            """
        },
        {
            "result.video_info.dash",
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":[{}],"dash":null}}}
            """
        },
        {
            "result.video_info.dash.video",
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":[],"dash":{"video":null,"audio":[{}]}}}}
            """
        },
        {
            "result.video_info.dash.audio",
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":[],"dash":{"video":[{}],"audio":null}}}}
            """
        }
    };

    public static TheoryData<string> EmbeddedBangumiPlaybackShapes => new()
    {
        """
        <script>const playurlSSRData = {"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"durl":[],"dash":{"video":[{"id":112}],"audio":[{"id":30280}]}}}};</script>
        """,
        """
        <script>const playurlSSRData = {"code":0,"raw":{"data":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"durl":[],"dash":{"video":[{"id":112}],"audio":[{"id":30280}]}}}}};</script>
        """,
        """
        <script>const playurlSSRData = {"data":{"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"durl":[],"dash":{"video":[{"id":112}],"audio":[{"id":30280}]}}}}};</script>
        """
    };

    private const string DegradedBangumiApiResponse =
        """
        {"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"quality":64,"accept_quality":[112,80,64],"support_formats":[{"quality":112,"need_login":true,"need_vip":true},{"quality":80},{"quality":64}],"durl":[],"dash":{"video":[{"id":64,"codecid":7,"base_url":"https://api.invalid/video-64"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio-30280"}]}}}}
        """;

    private const string BetterEmbeddedBangumiPage =
        """
        <html><script>const playurlSSRData = {"data":{"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"quality":112,"accept_quality":[112,80,64],"support_formats":[{"quality":112}],"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://media.invalid/video-112"},{"id":64,"codecid":7,"base_url":"https://media.invalid/video-64"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}}};</script></html>
        """;

    private static StubBilibiliApiClient CreateClient(
        string sampleName,
        Action<BilibiliHttpRequest>? observeRequest = null)
    {
        var body = File.ReadAllText(Path.Combine(SampleDirectory, sampleName));
        return new StubBilibiliApiClient((request, _) =>
        {
            observeRequest?.Invoke(request);
            return Task.FromResult(body);
        });
    }

    private static StubBilibiliApiClient CreateClientFromBody(string body) =>
        new((_, _) => Task.FromResult(body));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
