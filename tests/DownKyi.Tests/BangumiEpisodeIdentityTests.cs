using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

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
                "share_copy": "Episode"
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
                    "title": "Extra"
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
        "{\"code\":0,\"message\":\"success\",\"result\":{\"video_info\":{\"durl\":[{\"order\":1}]}}}";

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
        Assert.Collection(
            sections[1].VideoPages,
            sectionPage => Assert.Equal(EpisodeId, sectionPage.EpisodeId),
            sectionPage => Assert.Equal(2584322, sectionPage.EpisodeId));

        await service.GetVideoStreamAsync(
            page,
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

        await resolver.ResolveAsync(
            context,
            TestContext.Current.CancellationToken);

        AssertEpisodeId(capturedRequest);
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
            FilePath = Path.Combine(Path.GetTempPath(), "downkyi-bangumi-episode-identity")
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
