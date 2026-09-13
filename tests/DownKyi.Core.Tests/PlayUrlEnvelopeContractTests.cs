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
        Assert.Contains("qn=125", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fourk=1", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnver=0", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnval=4048", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("bvid=BV1fixture", requestUri.Query, StringComparison.Ordinal);
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
