using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class VideoParseCoordinatorTests
{
    [Fact]
    public async Task LoadDetailAsyncBuildsDefaultSectionBeforeReturning()
    {
        var info = new VideoInfoView { Title = "ready" };
        var pages = new List<VideoPage> { new() { Cid = 42 } };
        var service = new StubInfoService
        {
            VideoInfo = info,
            VideoPages = pages
        };
        var coordinator = new VideoParseCoordinator((_, _) => service);

        var result = await coordinator.LoadDetailAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            CancellationToken.None);

        Assert.Same(info, result.VideoInfoView);
        Assert.Equal(VideoDetailParseOutcome.Ready, result.Outcome);
        var section = Assert.Single(result.VideoSections);
        Assert.Equal("default", section.Title);
        Assert.True(section.IsSelected);
        Assert.Same(pages, section.VideoPages);
    }

    [Fact]
    public async Task LoadDetailAsyncReportsNoContentWhenMetadataHasNoPages()
    {
        var service = new StubInfoService
        {
            VideoInfo = new VideoInfoView { Title = "metadata only" },
            VideoPages = []
        };
        var coordinator = new VideoParseCoordinator((_, _) => service);

        var result = await coordinator.LoadDetailAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            CancellationToken.None);

        Assert.Equal(VideoDetailParseOutcome.NoContent, result.Outcome);
        Assert.Null(result.VideoInfoView);
        Assert.Empty(result.VideoSections);
    }

    [Fact]
    public async Task LoadDetailAsyncPropagatesCancellationBeforeReadingSections()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new StubInfoService
        {
            GetVideoInfo = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return new VideoInfoView();
            }
        };
        var replacementService = new StubInfoService
        {
            VideoInfo = new VideoInfoView { Title = "replacement" },
            VideoPages = [new VideoPage { Cid = 7 }]
        };
        var factoryCallCount = 0;
        var coordinator = new VideoParseCoordinator((_, _) =>
            Interlocked.Increment(ref factoryCallCount) == 1 ? service : replacementService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.LoadDetailAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            cancellation.Token));

        Assert.Equal(0, service.VideoSectionReadCount);
        Assert.Equal(0, service.VideoPageReadCount);

        var replacement = await coordinator.LoadDetailAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            CancellationToken.None);

        Assert.Equal(2, factoryCallCount);
        Assert.Equal(VideoDetailParseOutcome.Ready, replacement.Outcome);
        Assert.Equal(7, Assert.Single(Assert.Single(replacement.VideoSections).VideoPages).Cid);
    }

    private sealed class StubInfoService : IInfoService
    {
        public Func<CancellationToken, VideoInfoView?>? GetVideoInfo { get; init; }

        public VideoInfoView? VideoInfo { get; init; }

        public IList<VideoSection>? VideoSections { get; init; }

        public IList<VideoPage>? VideoPages { get; init; }

        public int VideoSectionReadCount { get; private set; }

        public int VideoPageReadCount { get; private set; }

        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            return GetVideoInfo?.Invoke(cancellationToken) ?? VideoInfo;
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoSectionReadCount++;
            return VideoSections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoPageReadCount++;
            return VideoPages;
        }

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            PlaybackDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<PlayUrl?>(new NotSupportedException());
        }
    }
}
