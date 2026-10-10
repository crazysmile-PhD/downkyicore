using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
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
        var coordinator = CreateCoordinator(factory);
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
        var coordinator = CreateCoordinator(factory);

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
        var coordinator = CreateCoordinator(factory);

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
        var coordinator = CreateCoordinator(new RecordingFactory(session));
        var video = new VideoInfoView();
        var page = new VideoPage
        {
            IsSelected = true,
            PlaybackAvailability = PlayUrlAvailability.From(new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 80,
                            CodecId = 7,
                            BaseAddress = "https://media.invalid/video-80"
                        }
                    ]
                }
            })
        };
        IList<VideoSection> sections = [new VideoSection { VideoPages = [page] }];

        var result = await coordinator.AddAsync(
            "BV17x411w7KC",
            video,
            sections,
            isAll: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result?.AddedCount);
        Assert.Equal(1, session.PrepareCount);
        Assert.Equal(1, session.AddCount);
        Assert.Same(video, session.CreatedPreparedDownload!.Video);
        Assert.Equal(session.Selection.Directory, session.ReceivedDirectory);
        Assert.Same(video, session.ReceivedFinalizedDownload!.Video);
        Assert.Same(page, session.ReceivedSubtitlePage);
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

        public string? ReceivedDirectory { get; private set; }

        public PreparedDownload? CreatedPreparedDownload { get; private set; }

        public FinalizedDownload? ReceivedFinalizedDownload { get; private set; }

        public VideoPage? ReceivedSubtitlePage { get; private set; }

        public Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmissionCheckCount++;
            return Task.FromResult(admissionAllowed);
        }

        public Task<DownloadAddSelection?> SelectDownloadAsync(
            VideoPage? subtitlePage = null,
            CancellationToken cancellationToken = default)
        {
            DirectorySelectionCount++;
            ReceivedSubtitlePage = subtitlePage;
            return Task.FromResult<DownloadAddSelection?>(Selection);
        }

        public Task<PreparedDownload> PrepareAsync(
            VideoInfoView videoInfoView,
            IList<VideoSection> videoSections,
            DownloadContentSelection requestedContent,
            bool isAll,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(isAll);
            Assert.Equal(Selection.RequestedContent, requestedContent);
            PrepareCount++;
            CreatedPreparedDownload = PreparedDownload.Create(videoInfoView, videoSections);
            return Task.FromResult(CreatedPreparedDownload);
        }

        public Task<PreparedDownload?> PrepareAsync(
            IInfoService videoInfoService,
            DownloadContentSelection requestedContent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DownloadAddResult> AddToDownload(
            string directory,
            FinalizedDownload finalizedDownload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            ReceivedDirectory = directory;
            ReceivedFinalizedDownload = finalizedDownload;
            return Task.FromResult(new DownloadAddResult(
                AddedCount: 1,
                DuplicateCount: 0,
                FailedCount: 0,
                SkippedCount: 0));
        }
    }

    private static VideoDetailDownloadCoordinator CreateCoordinator(
        IAddToDownloadServiceFactory factory)
    {
        var resolver = new DownloadContentConflictResolver(new UnexpectedDialogService());
        return new VideoDetailDownloadCoordinator(factory, new DownloadActionPlanner(resolver));
    }

    private sealed class UnexpectedDialogService : DownKyi.Application.Desktop.IAppDialogService
    {
        public Task<DownKyi.Application.Desktop.AppDialogResult> ShowAsync(
            DownKyi.Application.Desktop.AppDialogRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(
                $"Unexpected dialog: {request.Dialog}.");
    }
}
