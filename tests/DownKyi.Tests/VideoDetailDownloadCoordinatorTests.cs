using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
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

    [Fact]
    public async Task ExistingVideoDataIsPreparedBeforeTheExplicitSelectionIsAdded()
    {
        var session = new RecordingSession(admissionAllowed: true);
        var coordinator = new VideoDetailDownloadCoordinator(new RecordingFactory(session));
        var video = new VideoInfoView();
        IList<VideoSection> sections = [new VideoSection()];

        var result = await coordinator.AddAsync(
            "BV17x411w7KC",
            video,
            sections,
            isAll: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Equal(1, session.PrepareCount);
        Assert.Equal(1, session.AddCount);
        Assert.Same(video, session.CreatedPreparedDownload!.Video);
        Assert.Same(session.Selection, session.ReceivedSelection);
        Assert.Same(session.CreatedPreparedDownload, session.ReceivedPreparedDownload);
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

    private sealed class RecordingSession(bool admissionAllowed = false) : IAddToDownloadSession
    {
        public int AdmissionCheckCount { get; private set; }

        public int DirectorySelectionCount { get; private set; }

        public int AddCount { get; private set; }

        public int PrepareCount { get; private set; }

        public DownloadAddSelection Selection { get; } = new(
            @"D:\Downloads",
            DownloadContentSelection.None with { Video = true });

        public DownloadAddSelection? ReceivedSelection { get; private set; }

        public PreparedDownload? CreatedPreparedDownload { get; private set; }

        public PreparedDownload? ReceivedPreparedDownload { get; private set; }

        public Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmissionCheckCount++;
            return Task.FromResult(admissionAllowed);
        }

        public Task<DownloadAddSelection?> SelectDownloadAsync(
            CancellationToken cancellationToken = default)
        {
            DirectorySelectionCount++;
            return Task.FromResult<DownloadAddSelection?>(Selection);
        }

        public Task<PreparedDownload> PrepareAsync(
            VideoInfoView videoInfoView,
            IList<VideoSection> videoSections,
            bool isAll,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(isAll);
            PrepareCount++;
            CreatedPreparedDownload = PreparedDownload.Create(videoInfoView, videoSections);
            return Task.FromResult(CreatedPreparedDownload);
        }

        public Task<PreparedDownload?> PrepareAsync(
            IInfoService videoInfoService,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> AddToDownload(
            DownloadAddSelection selection,
            PreparedDownload preparedDownload,
            bool isAll = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            ReceivedSelection = selection;
            ReceivedPreparedDownload = preparedDownload;
            return Task.FromResult(1);
        }
    }
}
