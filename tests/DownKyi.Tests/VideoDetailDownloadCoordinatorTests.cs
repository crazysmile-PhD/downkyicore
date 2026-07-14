using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class VideoDetailDownloadCoordinatorTests
{
    [Fact]
    public async Task PreCanceledAddDoesNotCreateDownloadService()
    {
        var factory = new RecordingFactory();
        var coordinator = new VideoDetailDownloadCoordinator(factory);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            "BV17x411w7KC",
            new VideoInfoView(),
            [],
            isAll: false,
            new EventAggregator(),
            dialogService: null,
            cancellation.Token));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task UnsupportedInputDoesNotCreateDownloadService()
    {
        var factory = new RecordingFactory();
        var coordinator = new VideoDetailDownloadCoordinator(factory);

        var result = await coordinator.AddAsync(
            "not-a-video",
            new VideoInfoView(),
            [],
            isAll: false,
            new EventAggregator(),
            dialogService: null,
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, factory.CreateCount);
    }

    private sealed class RecordingFactory : IAddToDownloadServiceFactory
    {
        public int CreateCount { get; private set; }

        public AddToDownloadService Create(PlayStreamType streamType)
        {
            CreateCount++;
            throw new InvalidOperationException("A download service should not have been created.");
        }

        public AddToDownloadService Create(string id, PlayStreamType streamType)
        {
            CreateCount++;
            throw new InvalidOperationException("A download service should not have been created.");
        }
    }
}
