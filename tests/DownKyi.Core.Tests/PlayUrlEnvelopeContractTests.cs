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

    [Theory]
    [MemberData(nameof(OrdinaryDashMediaShapes))]
    public async Task OrdinaryVideoEndpointNormalizesAbsentDashMediaCollections(
        string responseBody,
        int expectedVideoCount,
        int expectedAudioCount)
    {
        var client = CreateClientFromBody(responseBody);

        var payload = await client.GetVideoPlayUrlAsync(
            Keys,
            1702204169,
            1,
            "BV1fixture",
            2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(payload);
        Assert.Equal(expectedVideoCount, payload.Dash.Video.Count);
        Assert.Equal(expectedAudioCount, payload.Dash.Audio.Count);
        var availability = PlayUrlAvailability.From(payload);
        Assert.Equal(expectedVideoCount, availability.Video.Count);
        Assert.Equal(expectedAudioCount, availability.Audio.Count);
    }

    [Fact]
    public async Task WebPageVideoEndpointAcceptsVideoOnlyDashWithNullAudio()
    {
        var requests = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return Task.FromResult(
                """
                <script>window.__playinfo__={"code":0,"message":"success","data":{"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":null}}}</script>
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

        Assert.Equal(1, requests);
        Assert.Empty(payload?.Dash.Audio ?? []);
        Assert.Equal(80, Assert.Single(payload?.Dash.Video ?? []).Id);
    }

    [Fact]
    public async Task OrdinaryVideoEndpointRejectsPayloadWithoutAnyMediaAfterNormalization()
    {
        var client = CreateClientFromBody(
            """
            {"code":0,"message":"success","data":{"durl":null,"dash":{"video":null}}}
            """);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetVideoPlayUrlAsync(
                Keys,
                1702204169,
                1,
                "BV1fixture",
                2,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetVideoPlayUrlAsync), exception.Operation);
        Assert.Contains("empty 'data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryVideoApiErrorRemainsApiResponseFailure()
    {
        var client = CreateClientFromBody(
            """
            {"code":-10403,"message":"restricted","data":{"dash":{"video":null,"audio":null}}}
            """);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetVideoPlayUrlAsync(
                Keys,
                1702204169,
                1,
                "BV1fixture",
                2,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetVideoPlayUrlAsync), exception.Operation);
        Assert.Equal(-10403, exception.Code);
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
        var requests = new List<BilibiliHttpRequest>();
        var client = CreateClient(
            "playurl-bangumi-v2-result.json",
            requests.Add);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 80,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(payload?.Durl ?? []).Order);
        Assert.Equal(2, requests.Count);
        Assert.Equal(
            "https://www.bilibili.com/bangumi/play/ep3489",
            requests[0].RequestAddress);
        var request = requests[1];
        var requestUri = new Uri(request.RequestAddress);
        Assert.Equal("/pgc/player/web/v2/playurl", requestUri.AbsolutePath);
        Assert.Contains("cid=2", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("ep_id=3489", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("qn=80", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fourk=1", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnver=0", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("fnval=4048", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("bvid=BV1fixture", requestUri.Query, StringComparison.Ordinal);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ep3489", request.Referer);
        Assert.Equal(80, payload?.Diagnostics?.RequestedQuality);
        Assert.Equal(4048, payload?.Diagnostics?.Fnval);
        Assert.Equal(PlayUrlResolutionSource.Api, payload?.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:embedded-playback-unavailable",
            payload?.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryUsesEmbeddedPageAsPrimarySource()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(BetterEmbeddedBangumiPage);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, payload!.Dash.Video.Count);
        Assert.Contains(payload.Dash.Video, video =>
            video.Id == 112
            && video.BaseAddress == "https://media.invalid/video-112");
        Assert.Equal(
            [112, 64],
            payload.Availability!.Video.Select(video => video.Quality).ToArray());
        Assert.Single(requests);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ep3489", requests[0].RequestAddress);
        Assert.Equal(requests[0].RequestAddress, requests[0].Referer);
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload.Diagnostics?.Source);
        Assert.Null(payload.Diagnostics?.Fnval);
        Assert.Equal("PLAY_WHOLE", payload?.Diagnostics?.PlayDetail);
        Assert.Equal("embedded-availability-selected", payload!.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryDoesNotCallApiWhenPageHasOrdinary1080P()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? Ordinary1080EmbeddedBangumiPage
                : DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(requests);
        Assert.Equal(2, payload!.Dash.Video.Count);
        Assert.Equal(
            [80, 64],
            payload.Availability!.Video.Select(video => video.Quality).ToArray());
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload.Diagnostics?.Source);
        Assert.Equal("embedded-availability-selected", payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryKeepsEmbeddedDurlManifestWithoutApiMerge()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? BetterEmbeddedBangumiDurlPage
                : DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(requests);
        Assert.Single(payload!.Durl);
        Assert.Empty(payload.Dash.Video);
        var availability = Assert.Single(payload.Availability!.Video);
        Assert.Equal(112, availability.Quality);
        Assert.Equal(PlayUrlStreamKind.Durl, availability.StreamKind);
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload.Diagnostics?.Source);
        Assert.Equal("embedded-availability-selected", payload.Diagnostics?.Outcome);
    }

    [Fact]
    public void AvailabilityIncludesOnlyAudioTracksWithUsableUrls()
    {
        var primary = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 64,
                        CodecId = 7,
                        BaseAddress = "https://api.invalid/video-64"
                    }
                ],
                Audio = [new PlayUrlDashVideo { Id = 30280, BaseAddress = " " }],
                Dolby = new PlayUrlDashDolby
                {
                    Audio = [new PlayUrlDashVideo { Id = 30250 }]
                },
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo { Id = 30251 }
                }
            }
        };
        var supplement = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 112,
                        CodecId = 13,
                        BaseAddress = "https://media.invalid/video-112"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        BackupUrl = ["https://media.invalid/audio"]
                    }
                ],
                Dolby = new PlayUrlDashDolby
                {
                    Audio =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30250,
                            BackupUrl = ["https://media.invalid/dolby"]
                        }
                    ]
                },
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo
                    {
                        Id = 30251,
                        BackupUrl = ["https://media.invalid/flac"]
                    }
                }
            }
        };

        var availability = BangumiPlaybackResolver.DiscoverAvailability(primary, supplement);

        Assert.Equal([30280, 30251, 30250], availability.Audio);
    }

    [Fact]
    public void DownloadSelectionKeepsOneSourceManifestAndOnlyItsUsableAudio()
    {
        var primary = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 64,
                        CodecId = 7,
                        BaseAddress = "https://api.invalid/video-64"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        BaseAddress = "https://api.invalid/audio-30280"
                    }
                ],
                Dolby = new PlayUrlDashDolby
                {
                    Audio = [new PlayUrlDashVideo { Id = 30250 }]
                },
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo
                    {
                        Id = 30251,
                        BaseAddress = "https://api.invalid/flac"
                    }
                }
            }
        };
        var supplement = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 112,
                        CodecId = 13,
                        BaseAddress = "https://media.invalid/video-112"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo { Id = 30280, BaseAddress = " " },
                    new PlayUrlDashVideo
                    {
                        Id = 30232,
                        BackupUrl = ["https://media.invalid/audio-30232"]
                    }
                ],
                Dolby = new PlayUrlDashDolby
                {
                    Audio =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30250,
                            BackupUrl = ["https://media.invalid/dolby"]
                        }
                    ]
                },
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo { Id = 30251, BaseAddress = " " }
                }
            }
        };

        var found = BangumiPlaybackResolver.TrySelectDownloadPlayback(
            primary,
            supplement,
            requestedQuality: 112,
            requestedCodecId: null,
            requestedAudioId: null,
            requestedStreamKind: null,
            out var selected);

        Assert.True(found);
        Assert.Same(supplement, selected);
        Assert.Equal([30232], selected!.Dash.Audio.Select(audio => audio.Id));
        Assert.Equal(
            "https://media.invalid/dolby",
            Assert.Single(Assert.Single(selected.Dash.Dolby!.Audio).BackupUrl));
        Assert.Null(selected.Dash.Flac);
        Assert.DoesNotContain(
            selected.Dash.Audio,
            audio => audio.BaseAddress.Contains("api.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BangumiDiscoveryDoesNotCreateAdvertisedQualityWithoutAStreamUrl()
    {
        var requests = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1
                ? """
                  <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":112,"support_formats":[{"quality":112}],"durl":[],"dash":{"video":[{"id":112,"codecid":13}],"audio":[]}}}};</script>
                  """
                : DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(64, Assert.Single(payload!.Availability!.Video).Quality);
        Assert.Equal(2, requests);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:embedded-playback-without-usable-address",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryFallsBackToApiWhenEmbeddedObjectsHaveNoUsableUrls()
    {
        var requests = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1
                ? """
                  <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13}],"audio":[]}}}};</script>
                  """
                : """
                  {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://api.invalid/video-112"}],"audio":[]}}}}
                  """);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requests);
        var availability = Assert.IsType<PlayUrlAvailability>(payload!.Availability);
        Assert.Contains(availability.Video, video =>
            video.Quality == 112
            && video.CodecId == 13
            && video.StreamKind == PlayUrlStreamKind.Dash);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:embedded-playback-without-usable-address",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDownloadSelectsRequestedEmbeddedPlaybackWithoutCallingApi()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? BetterEmbeddedBangumiPage
                : DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 112,
            videoCodecId: 13,
            audioId: 30280,
            streamKind: PlayUrlStreamKind.Dash,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(requests);
        Assert.Equal(
            "https://media.invalid/video-112",
            Assert.Single(payload!.Dash.Video).BaseAddress);
        Assert.DoesNotContain(payload.Dash.Video, video =>
            video.BaseAddress.Contains("api.invalid", StringComparison.Ordinal));
        Assert.Equal(
            "https://media.invalid/audio-30280",
            Assert.Single(payload.Dash.Audio).BaseAddress);
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload.Diagnostics?.Source);
        Assert.Equal("embedded-playback-selected", payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDownloadFallsBackToApiWhenEmbeddedPlaybackCannotSatisfySelection()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? Ordinary1080EmbeddedBangumiPage
                : """
                  {"code":0,"result":{"video_info":{"quality":74,"accept_quality":[112,80,74],"support_formats":[{"quality":112},{"quality":80},{"quality":74}],"durl":[],"dash":{"video":[{"id":74,"codecid":7,"base_url":"https://api.invalid/video-74"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio-30280"}]}}}}
                  """);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 74,
            videoCodecId: 7,
            audioId: 30280,
            streamKind: PlayUrlStreamKind.Dash,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requests.Count);
        Assert.Equal(
            "https://www.bilibili.com/bangumi/play/ep3489",
            requests[0].RequestAddress);
        Assert.Contains("qn=74", requests[1].RequestAddress, StringComparison.Ordinal);
        Assert.Equal("https://api.invalid/video-74", Assert.Single(payload!.Dash.Video).BaseAddress);
        Assert.Equal(
            "https://api.invalid/audio-30280",
            Assert.Single(payload.Dash.Audio).BaseAddress);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:embedded-playback-selection-unavailable",
            payload.Diagnostics?.Outcome);
    }

    [Theory]
    [InlineData(80, null, null, null)]
    [InlineData(112, 12, 30280, PlayUrlStreamKind.Dash)]
    [InlineData(112, 13, 30251, PlayUrlStreamKind.Dash)]
    [InlineData(112, 13, 30280, PlayUrlStreamKind.Durl)]
    public async Task BangumiDownloadClassifiesUnavailableSelection(
        int quality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind)
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? BetterEmbeddedBangumiPage
                : DegradedBangumiApiResponse);
        });

        var exception = await Assert.ThrowsAsync<PlaybackSelectionUnavailableException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                quality,
                videoCodecId,
                audioId,
                streamKind,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, requests.Count);
        Assert.Equal(quality, exception.Quality);
        Assert.Equal(videoCodecId, exception.VideoCodecId);
        Assert.Equal(audioId, exception.AudioId);
        Assert.Equal(streamKind, exception.StreamKind);
    }

    [Fact]
    public async Task BangumiDownloadDoesNotCombineWebPageAndApiSelections()
    {
        var requests = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1
                ? """
                  <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":64,"durl":[],"dash":{"video":[{"id":64,"codecid":7,"base_url":"https://media.invalid/video-64"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}};</script>
                  """
                : """
                  {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://api.invalid/video-112"}],"audio":[{"id":30232,"base_url":"https://api.invalid/audio-30232"}]}}}}
                  """);
        });

        var exception = await Assert.ThrowsAsync<PlaybackSelectionUnavailableException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                quality: 112,
                videoCodecId: 13,
                audioId: 30280,
                streamKind: PlayUrlStreamKind.Dash,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, requests);
        Assert.Equal(112, exception.Quality);
        Assert.Equal(13, exception.VideoCodecId);
        Assert.Equal(30280, exception.AudioId);
        Assert.Equal(PlayUrlStreamKind.Dash, exception.StreamKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BangumiPendingSelectionAcceptsVideoOnlyFromEitherSingleSource(
        bool videoIsPrimary)
    {
        var videoOnly = CreateDashVideoOnlyPlayback();
        var audioOnly = CreateDashAudioOnlyPlayback();

        var found = BangumiPlaybackResolver.TrySelectDownloadPlayback(
            videoIsPrimary ? videoOnly : audioOnly,
            videoIsPrimary ? audioOnly : videoOnly,
            requestedQuality: 112,
            requestedCodecId: 13,
            requestedAudioId: null,
            requestedStreamKind: PlayUrlStreamKind.Dash,
            requireVideo: true,
            out var selected);

        Assert.True(found);
        Assert.Same(videoOnly, selected);
        Assert.Equal(112, Assert.Single(selected!.Dash.Video).Id);
        Assert.Empty(selected.Dash.Audio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BangumiPendingSelectionAcceptsAudioOnlyFromEitherSingleSource(
        bool audioIsPrimary)
    {
        var videoOnly = CreateDashVideoOnlyPlayback();
        var audioOnly = CreateDashAudioOnlyPlayback();

        var found = BangumiPlaybackResolver.TrySelectDownloadPlayback(
            audioIsPrimary ? audioOnly : videoOnly,
            audioIsPrimary ? videoOnly : audioOnly,
            requestedQuality: 112,
            requestedCodecId: 13,
            requestedAudioId: 30280,
            requestedStreamKind: PlayUrlStreamKind.Dash,
            requireVideo: false,
            out var selected);

        Assert.True(found);
        Assert.Same(audioOnly, selected);
        Assert.Empty(selected!.Dash.Video);
        Assert.Equal(30280, Assert.Single(selected.Dash.Audio).Id);
    }

    [Fact]
    public void BangumiPendingSelectionRejectsVideoAndAudioSplitAcrossSources()
    {
        var found = BangumiPlaybackResolver.TrySelectDownloadPlayback(
            CreateDashVideoOnlyPlayback(),
            CreateDashAudioOnlyPlayback(),
            requestedQuality: 112,
            requestedCodecId: 13,
            requestedAudioId: 30280,
            requestedStreamKind: PlayUrlStreamKind.Dash,
            requireVideo: true,
            out var selected);

        Assert.False(found);
        Assert.Null(selected);
    }

    [Fact]
    public void BangumiPendingSelectionRejectsRequestWithoutPendingComponents()
    {
        var found = BangumiPlaybackResolver.TrySelectDownloadPlayback(
            CreateDashVideoOnlyPlayback(),
            CreateDashAudioOnlyPlayback(),
            requestedQuality: 112,
            requestedCodecId: 13,
            requestedAudioId: null,
            requestedStreamKind: PlayUrlStreamKind.Dash,
            requireVideo: false,
            out var selected);

        Assert.False(found);
        Assert.Null(selected);
    }

    [Fact]
    public async Task BangumiDownloadFallsBackToApiWhenPageHasNoEmbeddedPlayback()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? "<html><body>No embedded playback</body></html>"
                : """
                  {"code":0,"result":{"video_info":{"quality":74,"accept_quality":[112,74],"support_formats":[{"quality":112},{"quality":74}],"durl":[],"dash":{"video":[{"id":74,"codecid":7,"base_url":"https://api.invalid/video-74"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio"}]}}}}
                  """);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 74,
            videoCodecId: 7,
            audioId: 30280,
            streamKind: PlayUrlStreamKind.Dash,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requests.Count);
        Assert.Contains("qn=74", requests[1].RequestAddress, StringComparison.Ordinal);
        Assert.Equal(74, Assert.Single(payload!.Dash.Video).Id);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:embedded-playback-unavailable",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDownloadSelectsOneCompleteDurlManifest()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? BetterEmbeddedBangumiDurlPage
                : DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 112,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(112, payload!.Quality);
        Assert.Equal(
            "https://media.invalid/durl-112",
            Assert.Single(payload.Durl).SourceAddress);
        Assert.Empty(payload.Dash.Video);
        Assert.Single(requests);
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload.Diagnostics?.Source);
        Assert.Equal("embedded-playback-selected", payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryFallsBackToApiWhenWebPageTimesOut()
    {
        var requests = 0;
        var timeoutToken = new CancellationToken(canceled: true);
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return requests == 1
                ? Task.FromCanceled<string>(timeoutToken)
                : Task.FromResult(DegradedBangumiApiResponse);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(64, Assert.Single(payload!.Availability!.Video).Quality);
        Assert.Equal(2, requests);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:web-request-timeout",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDownloadFallsBackToApiWhenWebPageTimesOut()
    {
        var requests = 0;
        var timeoutToken = new CancellationToken(canceled: true);
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return requests == 1
                ? Task.FromCanceled<string>(timeoutToken)
                : Task.FromResult(
                    """
                    {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://api.invalid/video-112"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio-30280"}]}}}}
                    """);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 112,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requests);
        Assert.Equal(112, Assert.Single(payload!.Dash.Video).Id);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:web-request-timeout",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDownloadFallsBackToApiWhenWebPageRequestFails()
    {
        var requests = 0;
        var expected = new HttpRequestException("page transport failed");
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return requests == 1
                ? Task.FromException<string>(expected)
                : Task.FromResult(
                    """
                    {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://api.invalid/video-112"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio-30280"}]}}}}
                    """);
        });

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 112,
            videoCodecId: 13,
            audioId: 30280,
            streamKind: PlayUrlStreamKind.Dash,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requests);
        Assert.Equal(112, Assert.Single(payload!.Dash.Video).Id);
        Assert.Equal(PlayUrlResolutionSource.Api, payload.Diagnostics?.Source);
        Assert.Equal(
            "api-fallback-selected:web-request-failed:HttpRequestException",
            payload.Diagnostics?.Outcome);
    }

    [Fact]
    public async Task BangumiDiscoveryPropagatesCallerCancellation()
    {
        var requests = 0;
        using var caller = new CancellationTokenSource();
        var client = new StubBilibiliApiClient(async (_, _) =>
        {
            requests++;
            await caller.CancelAsync().ConfigureAwait(false);
            return await Task.FromCanceled<string>(caller.Token).ConfigureAwait(false);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetBangumiPlaybackDiscoveryAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: caller.Token));

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task BangumiDiscoveryUsesWebPageForLegitimate720PResponse()
    {
        var requests = new List<BilibiliHttpRequest>();
        var client = new StubBilibiliApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                """
                <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":64,"accept_quality":[64,32,16],"support_formats":[{"quality":64}],"durl":[],"dash":{"video":[{"id":64,"codecid":7,"base_url":"https://media.invalid/video-64"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio"}]}}}};</script>
                """);
        });

        var payload = await client.GetBangumiPlaybackDiscoveryAsync(
            1,
            "BV1fixture",
            2,
            3489,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(64, Assert.Single(payload?.Dash.Video ?? []).Id);
        Assert.Single(requests);
        Assert.Equal(PlayUrlResolutionSource.WebPage, payload?.Diagnostics?.Source);
        Assert.Equal("embedded-availability-selected", payload?.Diagnostics?.Outcome);
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
    [MemberData(nameof(BangumiSingleMediaShapes))]
    public void BangumiV2NormalizesAbsentOptionalMediaCollections(
        string responseBody,
        int expectedVideoCount,
        int expectedAudioCount)
    {
        var response = BiliApiRequest.ParseJson<BangumiPlayUrlV2Origin>(
            responseBody,
            "bangumi-v2");
        var payload = BangumiPlayUrlV2Contract.SelectPayload(response, "bangumi-v2");

        Assert.Equal(expectedVideoCount, payload.Dash.Video.Count);
        Assert.Equal(expectedAudioCount, payload.Dash.Audio.Count);
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
            {"code":0,"message":"success","result":{"video_info":{"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}}
            """);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 80,
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
            {"code":0,"message":"success","result":{"video_info":{"quality":80,"video_codecid":7,"is_preview":0,"durl":[{"order":1,"url":"https://media.invalid/segment.flv"}],"dash":{"video":[],"audio":[]}}}}
            """);

        var payload = await client.GetBangumiPlayUrlAsync(
            1,
            "BV1fixture",
            2,
            3489,
            quality: 80,
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
    public async Task BangumiV2ApiErrorRemainsApiResponseFailure()
    {
        var requests = 0;
        var client = new StubBilibiliApiClient((_, _) =>
        {
            requests++;
            return Task.FromResult(
                """
                {"code":-10403,"message":"restricted","result":{"video_info":{"durl":[],"dash":{"video":[],"audio":[]}}}}
                """);
        });

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            client.GetBangumiPlayUrlAsync(
                1,
                "BV1fixture",
                2,
                3489,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(nameof(VideoStreamApi.GetBangumiPlayUrlAsync), exception.Operation);
        Assert.Equal(-10403, exception.Code);
        Assert.Equal(2, requests);
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

    private static PlayUrl CreateDashVideoOnlyPlayback() =>
        new()
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 112,
                        CodecId = 13,
                        BaseAddress = "https://media.invalid/video-112"
                    }
                ]
            }
        };

    private static PlayUrl CreateDashAudioOnlyPlayback() =>
        new()
        {
            Dash = new PlayUrlDash
            {
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        BaseAddress = "https://media.invalid/audio-30280"
                    }
                ]
            }
        };

    public static TheoryData<string, int, int> BangumiSingleMediaShapes => new()
    {
        {
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":null,"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":null}}}}
            """,
            1,
            0
        },
        {
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":[],"dash":{"video":null,"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}}
            """,
            0,
            1
        },
        {
            """
            {"code":0,"message":"success","result":{"video_info":{"durl":null,"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}]}}}}
            """,
            1,
            0
        },
        {
            """
            {"code":0,"message":"success","result":{"video_info":{"quality":80,"video_codecid":7,"durl":[{"order":1,"url":"https://media.invalid/combined.mp4"}],"dash":null}}}
            """,
            0,
            0
        }
    };

    public static TheoryData<string, int, int> OrdinaryDashMediaShapes => new()
    {
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":null}}}
            """,
            1,
            0
        },
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":[]}}}
            """,
            1,
            0
        },
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}]}}}
            """,
            1,
            0
        },
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"video":null,"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}
            """,
            0,
            1
        },
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}
            """,
            0,
            1
        },
        {
            """
            {"code":0,"message":"success","data":{"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}
            """,
            1,
            1
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

    private const string Ordinary1080EmbeddedBangumiPage =
        """
        <html><script>const playurlSSRData = {"data":{"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"quality":80,"accept_quality":[80,64],"support_formats":[{"quality":80},{"quality":64}],"durl":[],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.invalid/video-80"},{"id":64,"codecid":7,"base_url":"https://media.invalid/video-64"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio-30280"}]}}}}};</script></html>
        """;

    private const string BetterEmbeddedBangumiDurlPage =
        """
        <html><script>const playurlSSRData = {"data":{"code":0,"result":{"play_check":{"play_detail":"PLAY_WHOLE"},"video_info":{"quality":112,"video_codecid":7,"accept_quality":[112,64],"support_formats":[{"quality":112},{"quality":64}],"durl":[{"order":1,"url":"https://media.invalid/durl-112"}],"dash":{"video":[],"audio":[]}}}}};</script></html>
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
