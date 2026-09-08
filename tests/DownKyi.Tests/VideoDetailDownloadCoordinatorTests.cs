using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;

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
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task BlockedAdmissionStopsBeforeVideoDetailDirectorySelection()
    {
        var session = new RecordingSession();
        var factory = new RecordingFactory(session);
        var coordinator = new VideoDetailDownloadCoordinator(factory);

        var result = await coordinator.AddAsync(
            "BV17x411w7KC",
            new VideoInfoView(),
            [],
            isAll: false,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, session.AdmissionCheckCount);
        Assert.Equal(0, session.DirectorySelectionCount);
        Assert.Equal(0, session.AddCount);
    }

    private sealed class RecordingFactory(IAddToDownloadSession? session = null)
        : IAddToDownloadServiceFactory
    {
        public int CreateCount { get; private set; }

        public IAddToDownloadSession Create(PlayStreamType streamType)
        {
            CreateCount++;
            return session
                ?? throw new InvalidOperationException("A download service should not have been created.");
        }

    }

    private sealed class RecordingSession : IAddToDownloadSession
    {
        public int AdmissionCheckCount { get; private set; }

        public int DirectorySelectionCount { get; private set; }

        public int AddCount { get; private set; }

        public Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmissionCheckCount++;
            return Task.FromResult(false);
        }

        public Task<string?> SetDirectory(CancellationToken cancellationToken = default)
        {
            DirectorySelectionCount++;
            return Task.FromResult<string?>(@"D:\Downloads");
        }

        public void SetVideoInfoService(IInfoService videoInfoService) =>
            throw new NotSupportedException();

        public void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections) =>
            throw new NotSupportedException();

        public void GetVideo() => throw new NotSupportedException();

        public Task ParseVideoAsync(
            IInfoService videoInfoService,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> AddToDownload(
            string? directory,
            bool isAll = false,
            CancellationToken cancellationToken = default)
        {
            AddCount++;
            return Task.FromResult(1);
        }
    }
}
