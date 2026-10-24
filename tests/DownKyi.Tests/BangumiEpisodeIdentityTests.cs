using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class BangumiEpisodeIdentityTests
{
    private const long EpisodeId = 2584321;
    private const string SeasonResponse =
        """
        {
          "code": 0,
          "message": "success",
          "result": {
            "episodes": [
              {
                "aid": 1,
                "bvid": "BV1fixture",
                "cid": 2,
                "id": 2584321,
                "ep_id": 0,
                "share_copy": "Episode",
                "duration": 848000
              }
            ],
            "positive": {
              "id": 1,
              "title": "Main"
            },
            "section": [
              {
                "id": 2,
                "title": "Extras",
                "episodes": [
                  {
                    "aid": 1,
                    "bvid": "BV1fixture",
                    "cid": 2,
                    "id": 2584321,
                    "ep_id": 0,
                    "title": "Extra",
                    "duration": 61000
                  },
                  {
                    "aid": 1,
                    "bvid": "BV1fixture",
                    "cid": 3,
                    "id": 0,
                    "ep_id": 2584322,
                    "title": "Fallback extra"
                  }
                ]
              }
            ]
          }
        }
        """;
    private const string PlaybackResponse =
        "{\"code\":0,\"message\":\"success\",\"result\":{\"video_info\":{\"quality\":80,\"video_codecid\":7,\"durl\":[{\"order\":1,\"url\":\"https://media.invalid/video.flv\"}]}}}";
    private const string Issue668SeasonResponse =
        """
        {
          "code": 0,
          "message": "success",
          "result": {
            "type": 4,
            "title": "凡人修仙传",
            "episodes": [],
            "positive": {
              "id": 1,
              "title": "Main"
            },
            "section": [
              {
                "id": 78974,
                "title": "UP论道",
                "type": 5,
                "episodes": [
                  {
                    "aid": 116729191601860,
                    "bvid": null,
                    "cid": 0,
                    "id": 215048,
                    "ep_id": 0,
                    "title": "独家对话：凡人导演带你看人界篇燃点天花板慕兰之战！",
                    "link": "https://www.bilibili.com/video/BV18CEY6oEyH",
                    "status": 0
                  }
                ]
              }
            ]
          }
        }
        """;
    private const string Issue668VideoViewResponse =
        """
        {
          "code": 0,
          "message": "success",
          "data": {
            "aid": 116729191601860,
            "bvid": "BV18CEY6oEyH",
            "title": "独家对话",
            "pages": [
              {
                "cid": 987654321,
                "page": 1,
                "part": "独家对话",
                "duration": 60
              }
            ]
          }
        }
        """;
    private const string OrdinaryPlaybackPageResponse =
        """
        <script>window.__playinfo__={"code":0,"data":{"quality":80,"video_codecid":7,"durl":[{"order":1,"url":"https://media.invalid/video.flv"}]}}</script>
        """;
    private const string OrdinaryPlaybackApiResponse =
        """
        {"code":0,"data":{"quality":127,"video_codecid":7,"durl":[{"order":1,"url":"https://media.invalid/video-api.flv"}]}}
        """;

    [Fact]
    public async Task InfoServiceUsesPositiveSeasonEpisodeIdForPagesSectionsAndPlayback()
    {
        using var settings = new TestSettingsStore();
        BilibiliHttpRequest? capturedRequest = null;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                var requestUri = new Uri(request.RequestAddress);
                if (requestUri.AbsolutePath == "/pgc/view/web/season")
                {
                    return Task.FromResult(SeasonResponse);
                }

                capturedRequest = request;
                return Task.FromResult(PlaybackResponse);
            }
        };
        var service = await BangumiInfoService.CreateAsync(
            "ss1",
            settings.Store,
            client,
            TestContext.Current.CancellationToken);

        var page = Assert.Single(service.GetVideoPages(TestContext.Current.CancellationToken));
        var sections = Assert.IsAssignableFrom<IList<VideoSection>>(
            service.GetVideoSections(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EpisodeId, page.EpisodeId);
        Assert.Equal("14m8s", page.Duration);
        Assert.Collection(
            sections[1].VideoPages,
            sectionPage =>
            {
                Assert.Equal(EpisodeId, sectionPage.EpisodeId);
                Assert.Equal("1m1s", sectionPage.Duration);
            },
            sectionPage =>
            {
                Assert.Equal(2584322, sectionPage.EpisodeId);
                Assert.Equal("N/A", sectionPage.Duration);
            });

        await service.GetVideoStreamAsync(
            page,
            new PlaybackDiscoveryRequest(80, 30280, RequireVideo: true, RequireAudio: false),
            TestContext.Current.CancellationToken);

        AssertEpisodeId(capturedRequest);
    }

    [Fact]
    public async Task DownloadResolverPassesPersistedEpisodeIdToPlaybackRequest()
    {
        using var settings = new TestSettingsStore();
        BilibiliHttpRequest? capturedRequest = null;
        var client = CreateClient(request => capturedRequest = request);
        var resolver = new DownloadPlaybackResolver(
            new TestWbiKeyProvider(),
            TimeProvider.System,
            client);
        var context = CreateBangumiContext(settings.Store.Current);

        var result = await resolver.ResolveAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        AssertEpisodeId(capturedRequest);
    }

    [Fact]
    public async Task Issue668UgcSectionEntryUsesOrdinaryPlaybackForPreparationAndDownload()
    {
        using var settings = new TestSettingsStore();
        var requests = new List<Uri>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                var requestUri = new Uri(request.RequestAddress);
                requests.Add(requestUri);
                if (requestUri.AbsolutePath == "/pgc/view/web/season")
                {
                    return Task.FromResult(Issue668SeasonResponse);
                }

                if (requestUri.AbsolutePath == "/x/web-interface/wbi/view")
                {
                    return Task.FromResult(Issue668VideoViewResponse);
                }

                if (requestUri.Host == "www.bilibili.com"
                    && requestUri.AbsolutePath.Contains("/video/BV18CEY6oEyH", StringComparison.Ordinal))
                {
                    return Task.FromResult(OrdinaryPlaybackPageResponse);
                }

                if (requestUri.AbsolutePath == "/x/player/wbi/playurl")
                {
                    return Task.FromResult(OrdinaryPlaybackApiResponse);
                }

                throw new InvalidOperationException($"Unexpected request path: {requestUri.AbsolutePath}");
            }
        };
        var service = await BangumiInfoService.CreateAsync(
            "ss28747",
            settings.Store,
            client,
            TestContext.Current.CancellationToken);
        var sections = Assert.IsAssignableFrom<IList<VideoSection>>(
            service.GetVideoSections(cancellationToken: TestContext.Current.CancellationToken));
        var sectionPage = Assert.Single(sections[1].VideoPages);
        sectionPage.IsSelected = true;

        Assert.Equal(116729191601860, sectionPage.Avid);
        Assert.Equal("BV18CEY6oEyH", sectionPage.Bvid);
        Assert.Equal(0, sectionPage.Cid);
        Assert.Equal(-1, sectionPage.EpisodeId);
        Assert.Equal(PlayStreamType.Video, sectionPage.PlaybackStreamType);

        var requestedContent = DownloadContentSelection.None with { Video = true };
        var preparation = new DownloadPreparationService(
            PlayStreamType.Bangumi,
            settings.Store,
            new VideoTagProvider(client),
            new TestWbiKeyProvider(),
            client,
            NullLogger.Instance);
        var prepared = await preparation.PrepareAsync(
            Assert.IsType<VideoInfoView>(service.GetVideoView(TestContext.Current.CancellationToken)),
            sections,
            requestedContent,
            isAll: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(987654321, sectionPage.Cid);
        Assert.Equal(1, sectionPage.Page);
        Assert.DoesNotContain(requests, request =>
            request.AbsolutePath == "/pgc/player/web/v2/playurl");
        Assert.Contains(requests, request =>
            request.AbsolutePath == "/x/web-interface/wbi/view");
        Assert.Contains(requests, request =>
            request.Host == "www.bilibili.com"
            && request.AbsolutePath.Contains("/video/BV18CEY6oEyH", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.AbsolutePath == "/x/player/wbi/playurl");

        var preparedSection = prepared.Sections[1];
        var preparedPage = Assert.Single(preparedSection.Pages);
        var item = DownloadTaskDraftFactory.Create(
            Path.GetTempPath(),
            prepared.Video,
            preparedSection.Section,
            prepared.Sections.Count,
            preparedPage.Page,
            preparedPage.VideoQuality,
            preparedPage.AudioQualityFormat,
            preparedPage.AvailableMedia,
            settings.Store.Current,
            requestedContent);
        Assert.Equal(PlayStreamType.Video, item.Downloading.PlayStreamType);

        var resolver = new DownloadPlaybackResolver(
            new TestWbiKeyProvider(),
            TimeProvider.System,
            client);
        var result = await resolver.ResolveAsync(
            DownloadExecutionContextTestFactory.Create(item, settings.Store.Current),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.DoesNotContain(requests, request =>
            request.AbsolutePath == "/pgc/player/web/v2/playurl");
    }

    private static TestBilibiliApiClient CreateClient(Action<BilibiliHttpRequest> observeRequest)
    {
        return new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                observeRequest(request);
                return Task.FromResult(PlaybackResponse);
            }
        };
    }

    private static DownloadExecutionContext CreateBangumiContext(ApplicationSettings settings)
    {
        var taskId = new DownloadTaskId("bangumi-episode-identity");
        var downloadBase = new DownloadBase
        {
            Id = taskId.Value,
            Avid = 1,
            Bvid = "BV1fixture",
            Cid = 2,
            EpisodeId = EpisodeId,
            FilePath = Path.Combine(Path.GetTempPath(), "downkyi-bangumi-episode-identity"),
            Resolution = new DownKyi.Core.BiliApi.BiliUtils.Quality
            {
                Id = 80,
                Name = "1080P"
            },
            VideoCodecName = "H.264/AVC",
            NeedDownloadContent = DownloadContentSelection.None with
            {
                Video = true,
                MediaKind = DownloadMediaKind.Durl
            }
        };
        var downloading = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.Downloading,
                PlayStreamType = PlayStreamType.Bangumi
            }
        };

        return DownloadExecutionContextTestFactory.Create(downloading, settings);
    }

    private static void AssertEpisodeId(BilibiliHttpRequest? capturedRequest)
    {
        var request = Assert.IsType<BilibiliHttpRequest>(capturedRequest);
        var requestUri = new Uri(request.RequestAddress);
        Assert.Equal("/pgc/player/web/v2/playurl", requestUri.AbsolutePath);
        Assert.Contains($"ep_id={EpisodeId}", requestUri.Query, StringComparison.Ordinal);
    }
}
