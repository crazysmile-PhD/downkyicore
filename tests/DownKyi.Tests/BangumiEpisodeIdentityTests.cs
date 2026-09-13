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
    private const long EpisodeId = 3489;
    private const string PlaybackResponse =
        "{\"code\":0,\"message\":\"success\",\"result\":{\"video_info\":{\"durl\":[{\"order\":1}]}}}";

    [Fact]
    public async Task InfoServicePassesPageEpisodeIdToPlaybackRequest()
    {
        using var settings = new TestSettingsStore();
        BilibiliHttpRequest? capturedRequest = null;
        var client = CreateClient(request => capturedRequest = request);
        var service = new BangumiInfoService(settings.Store, client);
        var page = new VideoPage
        {
            Avid = 1,
            Bvid = "BV1fixture",
            Cid = 2,
            EpisodeId = EpisodeId
        };

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
