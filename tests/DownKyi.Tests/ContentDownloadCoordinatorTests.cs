using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ContentDownloadCoordinatorTests
{
    [Fact]
    public async Task PreCanceledRequestDoesNotCreateDownloadSession()
    {
        var factory = new RecordingFactory(new RecordingSession(@"D:\Downloads"));
        var coordinator = CreateCoordinator(factory, new RecordingInfoServiceFactory());
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
        var coordinator = CreateCoordinator(factory, new RecordingInfoServiceFactory());

        var result = await coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, false)],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(0, 0), result);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, session.DirectorySelectionCount);
    }

    [Fact]
    public async Task CancelingDirectorySelectionDoesNotQueueItems()
    {
        var session = new RecordingSession(null);
        var factory = new RecordingFactory(session);
        var coordinator = CreateCoordinator(factory, new RecordingInfoServiceFactory());

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
        var coordinator = CreateCoordinator(factory, infoServiceFactory);

        var result = await coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem("https://www.bilibili.com/bangumi/media/md28223074", DownloadInfoKind.Bangumi, false)
            ],
            onlySelected: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(2, 0), result);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(PlayStreamType.Video, factory.StreamType);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(2, session.PrepareCount);
        Assert.Equal(2, session.AddCount);
        Assert.Equal(2, session.PrepareCountWhenFirstAdded);
        Assert.Equal(
            [DownloadInfoKind.Video, DownloadInfoKind.Bangumi],
            infoServiceFactory.CreatedKinds);
    }

    [Fact]
    public async Task IncrementalBatchQueuesACompletedChunkBeforePreparingTheNextChunk()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory());
        ContentDownloadBatchProgress? latestProgress = null;
        var progress = new InlineProgress<ContentDownloadBatchProgress>(value => latestProgress = value);
        var items = Enumerable.Range(1, 6)
            .Select(index => new ContentDownloadItem(
                $"BV1incremental{index}",
                DownloadInfoKind.Video,
                IsSelected: true))
            .ToArray();

        var result = await coordinator.AddIncrementallyAsync(
            items,
            onlySelected: false,
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(6, 0), result);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(6, session.PrepareCount);
        Assert.Equal(6, session.AddCount);
        Assert.Equal(2, session.BatchAddCount);
        Assert.Equal(5, session.PrepareCountWhenFirstAdded);
        Assert.Equal(6, latestProgress?.ProcessedCount);
        Assert.Equal(6, latestProgress?.TotalCount);
        Assert.Equal(6, latestProgress?.AddedCount);
    }

    [Fact]
    public async Task UnavailableVideoIsSkippedWithoutStoppingFollowingItems()
    {
        const string unavailableSource = "BV1unavailable";
        var session = new RecordingSession(@"D:\Downloads");
        var infoServiceFactory = new RecordingInfoServiceFactory(item =>
            item.Source == unavailableSource
                ? new BilibiliApiResponseException(
                    nameof(VideoInfo.VideoViewInfoAsync),
                    "Video is unavailable.",
                    code: 62002)
                : null);
        var coordinator = CreateCoordinator(new RecordingFactory(session), infoServiceFactory);

        var result = await coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem(unavailableSource, DownloadInfoKind.Video, true),
                new ContentDownloadItem("BV1xx411c7mD", DownloadInfoKind.Video, true)
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(2, 1), result);
        Assert.Equal(2, session.PrepareCount);
        Assert.Equal(2, session.AddCount);
        Assert.Equal(
            ["BV17x411w7KC", unavailableSource, "BV1xx411c7mD"],
            infoServiceFactory.CreatedSources);
    }

    [Fact]
    public async Task CancellationWinsWhenFinalUnavailableVideoResponseArrives()
    {
        const string unavailableSource = "BV1unavailable";
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession(@"D:\Downloads");
        var infoServiceFactory = new RecordingInfoServiceFactory(_ =>
        {
            cancellation.Cancel();
            return new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Video is unavailable.",
                code: 62002);
        });
        var coordinator = CreateCoordinator(new RecordingFactory(session), infoServiceFactory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            [new ContentDownloadItem(unavailableSource, DownloadInfoKind.Video, true)],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal([unavailableSource], infoServiceFactory.CreatedSources);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.AddCount);
    }

    [Theory]
    [InlineData(nameof(VideoInfo.VideoViewInfoAsync), -101)]
    [InlineData("OtherOperation", 62002)]
    public async Task OtherBilibiliApiFailuresStopTheBatchBeforeQueueMutation(string operation, int code)
    {
        const string failingSource = "BV1failure";
        var session = new RecordingSession(@"D:\Downloads");
        var infoServiceFactory = new RecordingInfoServiceFactory(item =>
            item.Source == failingSource
                ? new BilibiliApiResponseException(
                    operation,
                    "Authentication failed.",
                    code: code)
                : null);
        var coordinator = CreateCoordinator(new RecordingFactory(session), infoServiceFactory);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() => coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem(failingSource, DownloadInfoKind.Video, true),
                new ContentDownloadItem("BV1xx411c7mD", DownloadInfoKind.Video, true)
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(code, exception.Code);
        Assert.Equal(1, session.PrepareCount);
        Assert.Equal(0, session.AddCount);
        Assert.Equal(
            ["BV17x411w7KC", failingSource],
            infoServiceFactory.CreatedSources);
    }

    [Fact]
    public async Task UnpreparableItemIsSkippedWithoutReducingCompletedCount()
    {
        var session = new RecordingSession(
            @"D:\Downloads",
            skippedPreparationCalls: new HashSet<int> { 2 });
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory());

        var result = await coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem("BV1unprepared", DownloadInfoKind.Video, true),
                new ContentDownloadItem("BV1xx411c7mD", DownloadInfoKind.Video, true)
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(2, 1), result);
        Assert.Equal(3, session.PrepareCount);
        Assert.Equal(2, session.AddCount);
    }

    [Fact]
    public async Task CancellationDuringInfoCreationStopsBeforeSessionMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession(@"D:\Downloads");
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new CancelingInfoServiceFactory(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            [new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true)],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.AddCount);
    }

    [Fact]
    public async Task CancelingAfterFirstQueuedItemPreservesCompletedDownload()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession(
            @"D:\Downloads",
            afterAdd: addedCount =>
            {
                if (addedCount == 1)
                {
                    cancellation.Cancel();
                }
            });
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AddAsync(
            [
                new ContentDownloadItem("BV17x411w7KC", DownloadInfoKind.Video, true),
                new ContentDownloadItem("BV1xx411c7mD", DownloadInfoKind.Video, true)
            ],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal(1, session.AddCount);
        Assert.Equal(2, session.PrepareCount);
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

    }

    private sealed class RecordingSession(
        string? directory,
        Action<int>? afterAdd = null,
        IReadOnlySet<int>? skippedPreparationCalls = null) : IAddToDownloadSession
    {
        public int DirectorySelectionCount { get; private set; }

        public int PrepareCount { get; private set; }

        public int AddCount { get; private set; }

        public int BatchAddCount { get; private set; }

        public int? PrepareCountWhenFirstAdded { get; private set; }

        public DownloadAddSelection Selection { get; } = new(
            @"D:\Downloads",
            DownloadContentSelection.None with { Video = true });

        public Task<DownloadSelectionResult> SelectDownloadAsync(
            VideoPage? subtitlePage = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectorySelectionCount++;
            return Task.FromResult(directory == null
                ? DownloadSelectionResult.Canceled
                : DownloadSelectionResult.Accepted(
                    new DownloadAddSelection(
                        directory,
                        Selection.RequestedContent)));
        }

        public Task<PreparedDownload> PrepareAsync(
            VideoInfoView videoInfoView,
            IList<VideoSection> videoSections,
            DownloadContentSelection requestedContent,
            bool isAll,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PreparedDownload?> PrepareAsync(
            IInfoService videoInfoService,
            DownloadContentSelection requestedContent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(videoInfoService);
            Assert.Equal(Selection.RequestedContent, requestedContent);
            PrepareCount++;
            if (skippedPreparationCalls?.Contains(PrepareCount) == true)
            {
                return Task.FromResult<PreparedDownload?>(null);
            }

            return Task.FromResult<PreparedDownload?>(PreparedDownload.Create(
                new VideoInfoView(),
                [new VideoSection()]));
        }

        public Task<DownloadAddResult> AddToDownloadBatch(
            DownloadAddSelection selection,
            IReadOnlyList<FinalizedDownload> finalizedDownloads,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(directory, selection.Directory);
            BatchAddCount++;
            foreach (var finalizedDownload in finalizedDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.NotNull(finalizedDownload);
                PrepareCountWhenFirstAdded ??= PrepareCount;
                AddCount++;
                afterAdd?.Invoke(AddCount);
            }
            return Task.FromResult(DownloadAddResult.FromCounts(
                addedCount: finalizedDownloads.Count,
                duplicateCount: 0,
                failedCount: 0,
                skippedCount: 0));
        }
    }

    private static ContentDownloadCoordinator CreateCoordinator(
        IAddToDownloadServiceFactory factory,
        IContentInfoServiceFactory infoServiceFactory)
    {
        var resolver = new DownloadContentConflictResolver(new UnexpectedDialogService());
        return new ContentDownloadCoordinator(
            factory,
            infoServiceFactory,
            new DownloadActionPlanner(resolver),
            NullLogger<ContentDownloadCoordinator>.Instance);
    }

    private sealed class UnexpectedDialogService : DownKyi.Application.Desktop.IAppDialogService
    {
        public Task<DownKyi.Application.Desktop.AppDialogResult> ShowAsync(
            DownKyi.Application.Desktop.AppDialogRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(
                $"Unexpected dialog: {request.Dialog}.");
    }

    private sealed class RecordingInfoServiceFactory(
        Func<ContentDownloadItem, Exception?>? failure = null) : IContentInfoServiceFactory
    {
        public List<DownloadInfoKind> CreatedKinds { get; } = [];

        public List<string> CreatedSources { get; } = [];

        public Task<IInfoService> CreateAsync(
            ContentDownloadItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedKinds.Add(item.Kind);
            CreatedSources.Add(item.Source);
            if (failure?.Invoke(item) is { } exception)
            {
                return Task.FromException<IInfoService>(exception);
            }

            return Task.FromResult<IInfoService>(new RecordingInfoService());
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

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<PlayUrl?>(new NotSupportedException());
        }

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            PlaybackDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<PlayUrl?>(new NotSupportedException());
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CancelingInfoServiceFactory(CancellationTokenSource cancellation)
        : IContentInfoServiceFactory
    {
        public async Task<IInfoService> CreateAsync(
            ContentDownloadItem item,
            CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should have interrupted info creation.");
        }
    }
}
