using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Services;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class VideoDetailWorkflowCoordinatorTests
{
    [Fact]
    public void StartingNewOperationCancelsPreviousGeneration()
    {
        using var settings = new TestSettingsStore();
        using var coordinator = new VideoDetailWorkflowCoordinator(
            settings.Store,
            new VideoTagProvider(),
            new TestWbiKeyProvider());

        var first = coordinator.StartOperation();
        var second = coordinator.StartOperation();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public async Task DetailLoadAndSearchReuseOriginalPageObjects()
    {
        var alpha = new VideoPage { Cid = 1, Name = "Alpha" };
        var beta = new VideoPage { Cid = 2, Name = "Beta" };
        var section = new VideoSection
        {
            Id = 1,
            VideoPages = [alpha, beta]
        };
        var parseCoordinator = new VideoParseCoordinator((_, _) => new StubInfoService
        {
            VideoInfo = new VideoInfoView(),
            VideoSections = [section]
        });
        using var coordinator = new VideoDetailWorkflowCoordinator(parseCoordinator, new VideoSearchState());
        var operation = coordinator.StartOperation();
        coordinator.Reset();
        coordinator.SetInput("BV17x411w7KC");

        var result = await coordinator.LoadDetailAsync(operation);
        coordinator.ApplySearch("Beta");

        Assert.Same(section, Assert.Single(result.VideoSections));
        Assert.Same(beta, Assert.Single(section.VideoPages));
    }

    private sealed class StubInfoService : IInfoService
    {
        public VideoInfoView? VideoInfo { get; init; }

        public IList<VideoSection>? VideoSections { get; init; }

        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VideoInfo;
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VideoSections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<PlayUrl?>(new NotSupportedException());
        }
    }
}
