using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class ContentDownloadCoordinatorTests
{
    [Fact]
    public async Task PreCanceledRequestDoesNotCreateDownloadSession()
    {
        var factory = new RecordingFactory(new RecordingSession(@"D:\Downloads"));
        var coordinator = new ContentDownloadCoordinator(factory, new RecordingInfoServiceFactory());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true)],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task EmptySelectionDoesNotCreateSessionOrOpenDirectory()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var factory = new RecordingFactory(session);
        var coordinator = new ContentDownloadCoordinator(factory, new RecordingInfoServiceFactory());

        var result = await coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, false)],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, session.DirectorySelectionCount);
    }

    [Fact]
    public async Task CancelingDirectorySelectionDoesNotQueueItems()
    {
        var session = new RecordingSession(null);
        var factory = new RecordingFactory(session);
        var coordinator = new ContentDownloadCoordinator(factory, new RecordingInfoServiceFactory());

        var result = await coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true)],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(PlayStreamType.Video, factory.StreamType);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(0, session.AddCount);
    }

    [Fact]
    public async Task MixedItemsShareOneDirectorySelectionAndQueueInOrder()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var factory = new RecordingFactory(session);
        var infoServiceFactory = new RecordingInfoServiceFactory();
        var coordinator = new ContentDownloadCoordinator(factory, infoServiceFactory);

        var result = await coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem("https://www.bilibili.com/bangumi/media/md28223074", DownloadInfoKind.Bangumi, false)
            ],
            onlySelected: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(PlayStreamType.Video, factory.StreamType);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(2, session.SetInfoCount);
        Assert.Equal(2, session.GetVideoCount);
        Assert.Equal(2, session.ParseCount);
        Assert.Equal(2, session.AddCount);
        Assert.Equal(
            [DownloadInfoKind.Video, DownloadInfoKind.Bangumi],
            infoServiceFactory.CreatedKinds);
    }

    [Fact]
    public async Task CancellationDuringInfoCreationStopsBeforeSessionMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession(@"D:\Downloads");
        var coordinator = new ContentDownloadCoordinator(
            new RecordingFactory(session),
            new CancelingInfoServiceFactory(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true)],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(0, session.SetInfoCount);
        Assert.Equal(0, session.AddCount);
    }

    private sealed class RecordingFactory(IAddToDownloadSession session) : IAddToDownloadServiceFactory
    {
        public int CreateCount { get; private set; }

        public PlayStreamType? StreamType { get; private set; }

        public IAddToDownloadSession Create(PlayStreamType streamType)
        {
            CreateCount++;
            StreamType = streamType;
            return session;
        }

        public IAddToDownloadSession Create(string id, PlayStreamType streamType)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingSession(string? directory) : IAddToDownloadSession
    {
        public int DirectorySelectionCount { get; private set; }

        public int SetInfoCount { get; private set; }

        public int GetVideoCount { get; private set; }

        public int ParseCount { get; private set; }

        public int AddCount { get; private set; }

        public Task<string?> SetDirectory(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectorySelectionCount++;
            return Task.FromResult(directory);
        }

        public void SetVideoInfoService(IInfoService videoInfoService)
        {
            Assert.NotNull(videoInfoService);
            SetInfoCount++;
        }

        public void GetVideo(VideoInfoView videoInfoView, IList<VideoSection> videoSections)
        {
            throw new NotSupportedException();
        }

        public void GetVideo()
        {
            GetVideoCount++;
        }

        public void ParseVideo(IInfoService videoInfoService)
        {
            Assert.NotNull(videoInfoService);
            ParseCount++;
        }

        public Task<int> AddToDownload(
            string? directoryPath,
            bool isAll = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(directory, directoryPath);
            Assert.False(isAll);
            AddCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class RecordingInfoServiceFactory : IContentInfoServiceFactory
    {
        public List<DownloadInfoKind> CreatedKinds { get; } = [];

        public IInfoService Create(ContentDownloadItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedKinds.Add(item.Kind);
            return new RecordingInfoService();
        }
    }

    private sealed class RecordingInfoService : IInfoService
    {
        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public PlayUrl? GetVideoStream(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CancelingInfoServiceFactory(CancellationTokenSource cancellation)
        : IContentInfoServiceFactory
    {
        public IInfoService Create(ContentDownloadItem item, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should have interrupted info creation.");
        }
    }
}
