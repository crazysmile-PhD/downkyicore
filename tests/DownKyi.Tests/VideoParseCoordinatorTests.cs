using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Services;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

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
            "BV1test",
            refresh: true,
            CancellationToken.None);

        Assert.Same(info, result.VideoInfoView);
        var section = Assert.Single(result.VideoSections);
        Assert.Equal("default", section.Title);
        Assert.True(section.IsSelected);
        Assert.Same(pages, section.VideoPages);
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
        var replacementPlayUrl = new PlayUrl();
        var replacementService = new StubInfoService
        {
            GetStream = (_, _) => replacementPlayUrl
        };
        var factoryCallCount = 0;
        var coordinator = new VideoParseCoordinator((_, _) =>
            Interlocked.Increment(ref factoryCallCount) == 1 ? service : replacementService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.LoadDetailAsync(
            "BV1test",
            refresh: true,
            cancellation.Token));

        Assert.Equal(0, service.VideoSectionReadCount);
        Assert.Equal(0, service.VideoPageReadCount);

        var page = new VideoPage { Cid = 7 };
        var streamResults = await coordinator.LoadPageStreamsAsync(
            "BV1test",
            [page],
            CancellationToken.None);

        Assert.Equal(2, factoryCallCount);
        Assert.Same(replacementPlayUrl, Assert.Single(streamResults).PlayUrl);
    }

    [Fact]
    public async Task LoadPageStreamsAsyncReturnsDataWithoutMutatingBoundPages()
    {
        var page = new VideoPage { Cid = 42 };
        var playUrl = new PlayUrl();
        var service = new StubInfoService
        {
            GetStream = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return playUrl;
            }
        };
        var coordinator = new VideoParseCoordinator((_, _) => service);

        var results = await coordinator.LoadPageStreamsAsync(
            "BV1test",
            [page],
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Same(page, result.Page);
        Assert.Same(playUrl, result.PlayUrl);
        Assert.Null(page.PlayUrl);
    }

    [Fact]
    public async Task LoadPageStreamsAsyncDoesNotReuseServiceForDifferentInput()
    {
        var firstPlayUrl = new PlayUrl();
        var secondPlayUrl = new PlayUrl();
        var firstService = new StubInfoService { GetStream = (_, _) => firstPlayUrl };
        var secondService = new StubInfoService { GetStream = (_, _) => secondPlayUrl };
        var factoryCallCount = 0;
        var coordinator = new VideoParseCoordinator((_, _) =>
            Interlocked.Increment(ref factoryCallCount) == 1 ? firstService : secondService);

        var firstResults = await coordinator.LoadPageStreamsAsync(
            "BV1first",
            [new VideoPage { Cid = 1 }],
            CancellationToken.None);
        var secondResults = await coordinator.LoadPageStreamsAsync(
            "BV1second",
            [new VideoPage { Cid = 2 }],
            CancellationToken.None);

        Assert.Equal(2, factoryCallCount);
        Assert.Same(firstPlayUrl, Assert.Single(firstResults).PlayUrl);
        Assert.Same(secondPlayUrl, Assert.Single(secondResults).PlayUrl);
    }

    private sealed class StubInfoService : IInfoService
    {
        public Func<CancellationToken, VideoInfoView?>? GetVideoInfo { get; init; }

        public Func<VideoPage, CancellationToken, PlayUrl?>? GetStream { get; init; }

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
            CancellationToken cancellationToken = default)
        {
            var result = GetStream?.Invoke(page, cancellationToken)
                         ?? throw new NotSupportedException();
            return Task.FromResult<PlayUrl?>(result);
        }
    }
}
