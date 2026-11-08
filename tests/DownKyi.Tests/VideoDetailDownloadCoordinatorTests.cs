using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
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
        var coordinator = CreateCoordinator(factory);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            new VideoInfoView(),
            [],
            isAll: false,
            cancellation.Token));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CanceledSelectionReturnsCanceledWithoutCallingPreparation()
    {
        var session = new RecordingSession(selectionAccepted: false);
        var coordinator = CreateCoordinator(new RecordingFactory(session));

        var result = await coordinator.AddAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            new VideoInfoView(),
            [],
            isAll: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadAddOutcome.Canceled, result.Outcome);
        Assert.Equal(DownloadAddStopReason.SelectionCanceled, result.StopReason);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.AddCount);
    }

    [Fact]
    public async Task SelectedVideoWithoutPreparsedPlaybackIsPreparedBeforeItIsAdded()
    {
        var session = new RecordingSession();
        var coordinator = CreateCoordinator(new RecordingFactory(session));
        var video = new VideoInfoView();
        var page = new VideoPage
        {
            IsSelected = true
        };
        IList<VideoSection> sections = [new VideoSection { VideoPages = [page] }];

        var result = await coordinator.AddAsync(
            new BilibiliBvidInput("BV17x411w7KC"),
            video,
            sections,
            isAll: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AddedCount);
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

    private sealed class RecordingSession(bool selectionAccepted = true) : IAddToDownloadSession
    {
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

        public Task<DownloadSelectionResult> SelectDownloadAsync(
            VideoPage? subtitlePage = null,
            CancellationToken cancellationToken = default)
        {
            DirectorySelectionCount++;
            ReceivedSubtitlePage = subtitlePage;
            return Task.FromResult(selectionAccepted
                ? DownloadSelectionResult.Accepted(Selection)
                : DownloadSelectionResult.Canceled);
        }

        public Task<PreparedDownload> PrepareAsync(
            VideoInfoView videoInfoView,
            IList<VideoSection> videoSections,
            DownloadContentSelection requestedContent,
            bool isAll,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(isAll);
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

        public Task<DownloadAddResult> AddToDownloadBatch(
            DownloadAddSelection selection,
            IReadOnlyList<FinalizedDownload> finalizedDownloads,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            ReceivedDirectory = selection.Directory;
            ReceivedFinalizedDownload = Assert.Single(finalizedDownloads);
            return Task.FromResult(DownloadAddResult.FromCounts(
                addedCount: 1,
                duplicateCount: 0,
                failedCount: 0,
                skippedCount: 0));
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
