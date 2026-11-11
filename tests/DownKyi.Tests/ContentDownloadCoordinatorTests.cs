using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
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
            [Item("BV17x411w7KC")],
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
            [Item("BV17x411w7KC", isSelected: false)],
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
            [Item("BV17x411w7KC")],
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
                Item("BV17x411w7KC"),
                Item("https://www.bilibili.com/bangumi/media/md28223074", isSelected: false)
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
            [typeof(BilibiliBvidInput), typeof(BilibiliBangumiMediaInput)],
            infoServiceFactory.CreatedInputTypes);
    }

    [Fact]
    public async Task CollectionBatchPreparesEveryItemBeforeSingleAdmission()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory());
        ContentDownloadBatchProgress? latestProgress = null;
        var progress = new InlineProgress<ContentDownloadBatchProgress>(value => latestProgress = value);
        var items = Enumerable.Range(1, 6)
            .Select(index => new ContentDownloadItem(
                new BilibiliAvidInput(index),
                IsSelected: true))
            .ToArray();

        var result = await coordinator.AddAsync(
            items,
            onlySelected: false,
            TestContext.Current.CancellationToken,
            progress);

        Assert.Equal(new ContentDownloadBatchResult(6, 0), result);
        Assert.Equal(1, session.DirectorySelectionCount);
        Assert.Equal(6, session.PrepareCount);
        Assert.Equal(6, session.AddCount);
        Assert.Equal(1, session.BatchAddCount);
        Assert.Equal(6, session.PrepareCountWhenFirstAdded);
        Assert.Equal(6, latestProgress?.ProcessedCount);
        Assert.Equal(6, latestProgress?.TotalCount);
    }

    [Fact]
    public async Task UnavailableVideoCanBeSkippedWithoutStoppingFollowingItems()
    {
        const string unavailableSource = "BV1xx411c7mD";
        var session = new RecordingSession(@"D:\Downloads");
        var dialogService = new RecordingUnavailableDialogService(
            DownloadUnavailableBatchAction.SkipUnavailable);
        var infoServiceFactory = new RecordingInfoServiceFactory(item =>
            SourceOf(item) == unavailableSource
                ? new BilibiliApiResponseException(
                    nameof(VideoInfo.VideoViewInfoAsync),
                    "Video is unavailable.",
                    code: 62002)
                : null);
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            infoServiceFactory,
            dialogService);

        var result = await coordinator.AddAsync(
            [
                Item("BV17x411w7KC"),
                Item(unavailableSource, displayName: "没有试看内容的充电影片"),
                Item("BV1G1421D7mL")
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(2, 1), result);
        Assert.Equal(2, session.PrepareCount);
        Assert.Equal(2, session.AddCount);
        Assert.Equal(
            ["BV17x411w7KC", unavailableSource, "BV1G1421D7mL"],
            infoServiceFactory.CreatedSources);
        var promptItem = Assert.Single(Assert.Single(dialogService.Prompts).Items);
        Assert.Equal("没有试看内容的充电影片", promptItem.DisplayName);
        Assert.Equal(unavailableSource, promptItem.Source);
        Assert.Equal(DownloadUnavailableReason.VideoUnavailable, promptItem.Reason);
        Assert.Equal(62002, promptItem.ApiCode);
    }

    [Fact]
    public async Task CancellationWinsWhenFinalUnavailableVideoResponseArrives()
    {
        const string unavailableSource = "BV1xx411c7mD";
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
            [Item(unavailableSource)],
            onlySelected: true,
            cancellation.Token));

        Assert.Equal([unavailableSource], infoServiceFactory.CreatedSources);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.AddCount);
    }

    [Fact]
    public async Task MultiplePreparationFailuresAreCollectedBeforeOneSkipDecision()
    {
        const string accountSource = "BV1Q541167Qg";
        const string restrictedSource = "BV1xx411c7mD";
        const string unavailableSource = "BV1G1421D7mL";
        const string unknownSource = "BV17x411w7KC";
        const string successfulSource = "BV1mK4y1C7Bz";
        var session = new RecordingSession(@"D:\Downloads");
        var dialogService = new RecordingUnavailableDialogService(
            DownloadUnavailableBatchAction.SkipUnavailable);
        var infoServiceFactory = new RecordingInfoServiceFactory(item => SourceOf(item) switch
        {
            accountSource => new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Authentication failed.",
                code: -101),
            restrictedSource => new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Nothing here.",
                code: -404),
            unavailableSource => new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Video is unavailable.",
                code: 62002),
            unknownSource => new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Unexpected response shape."),
            _ => null
        });
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            infoServiceFactory,
            dialogService);

        var result = await coordinator.AddAsync(
            [
                Item(accountSource, displayName: "需要登录"),
                Item(restrictedSource, displayName: "无权访问"),
                Item(unavailableSource, displayName: "稿件不可见"),
                Item(unknownSource, displayName: "未知失败"),
                Item(successfulSource, displayName: "可下载影片")
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(1, 4), result);
        Assert.Equal(1, session.PrepareCount);
        Assert.Equal(1, session.AddCount);
        Assert.Equal(
            [accountSource, restrictedSource, unavailableSource, unknownSource, successfulSource],
            infoServiceFactory.CreatedSources);
        var prompt = Assert.Single(dialogService.Prompts);
        Assert.Collection(
            prompt.Items,
            item =>
            {
                Assert.Equal("需要登录", item.DisplayName);
                Assert.Equal(DownloadUnavailableReason.AccountRequired, item.Reason);
                Assert.Equal(-101, item.ApiCode);
            },
            item =>
            {
                Assert.Equal("无权访问", item.DisplayName);
                Assert.Equal(DownloadUnavailableReason.AccessRestrictedOrUnavailable, item.Reason);
                Assert.Equal(-404, item.ApiCode);
            },
            item =>
            {
                Assert.Equal("稿件不可见", item.DisplayName);
                Assert.Equal(DownloadUnavailableReason.VideoUnavailable, item.Reason);
                Assert.Equal(62002, item.ApiCode);
            },
            item =>
            {
                Assert.Equal("未知失败", item.DisplayName);
                Assert.Equal(DownloadUnavailableReason.Unknown, item.Reason);
                Assert.Null(item.ApiCode);
            });
    }

    [Fact]
    public async Task CancelingUnavailableBatchDoesNotQueuePreparedItems()
    {
        const string failingSource = "BV1xx411c7mD";
        var session = new RecordingSession(@"D:\Downloads");
        var dialogService = new RecordingUnavailableDialogService(
            DownloadUnavailableBatchAction.CancelBatch);
        var infoServiceFactory = new RecordingInfoServiceFactory(item =>
            SourceOf(item) == failingSource
                ? new BilibiliApiResponseException(
                    nameof(VideoInfo.VideoViewInfoAsync),
                    "Nothing here.",
                    code: -404)
                : null);
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            infoServiceFactory,
            dialogService);

        var result = await coordinator.AddAsync(
            [
                Item("BV17x411w7KC"),
                Item(failingSource),
                Item("BV1G1421D7mL")
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(2, session.PrepareCount);
        Assert.Equal(0, session.BatchAddCount);
        Assert.Equal(0, session.AddCount);
        Assert.Single(dialogService.Prompts);
    }

    [Fact]
    public async Task UnpreparableItemIsSkippedWithoutReducingCompletedCount()
    {
        var session = new RecordingSession(
            @"D:\Downloads",
            skippedPreparationCalls: new HashSet<int> { 2 });
        var dialogService = new RecordingUnavailableDialogService(
            DownloadUnavailableBatchAction.SkipUnavailable);
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory(),
            dialogService);

        var result = await coordinator.AddAsync(
            [
                Item("BV17x411w7KC"),
                Item("BV1G1421D7mL"),
                Item("BV1xx411c7mD")
            ],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(2, 1), result);
        Assert.Equal(3, session.PrepareCount);
        Assert.Equal(2, session.AddCount);
        var promptItem = Assert.Single(Assert.Single(dialogService.Prompts).Items);
        Assert.Equal(DownloadUnavailableReason.NoDownloadData, promptItem.Reason);
    }

    [Fact]
    public async Task SkippingWhenEveryItemIsUnavailableDoesNotRunEmptyAdmission()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var dialogService = new RecordingUnavailableDialogService(
            DownloadUnavailableBatchAction.SkipUnavailable);
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory(_ => new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Nothing here.",
                code: -404)),
            dialogService);

        var result = await coordinator.AddAsync(
            [Item("BV17x411w7KC"), Item("BV1G1421D7mL")],
            onlySelected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ContentDownloadBatchResult(0, 2), result);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.BatchAddCount);
        Assert.Equal(0, session.AddCount);
        Assert.Single(dialogService.Prompts);
    }

    [Fact]
    public async Task ProgrammerFailureIsNotHiddenBehindUnavailableDialog()
    {
        var session = new RecordingSession(@"D:\Downloads");
        var coordinator = CreateCoordinator(
            new RecordingFactory(session),
            new RecordingInfoServiceFactory(_ =>
                new InvalidOperationException("Implementation defect.")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AddAsync(
                [Item("BV17x411w7KC")],
                onlySelected: true,
                TestContext.Current.CancellationToken));

        Assert.Equal("Implementation defect.", exception.Message);
        Assert.Equal(0, session.PrepareCount);
        Assert.Equal(0, session.BatchAddCount);
        Assert.Equal(0, session.AddCount);
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
            [Item("BV17x411w7KC")],
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
                Item("BV17x411w7KC"),
                Item("BV1xx411c7mD")
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
        IReadOnlySet<int>? skippedPreparationCalls = null,
        Func<int, DownloadAddResult>? batchResult = null) : IAddToDownloadSession
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
            if (batchResult?.Invoke(BatchAddCount) is { } configuredResult)
            {
                return Task.FromResult(configuredResult);
            }

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
        IContentInfoServiceFactory infoServiceFactory,
        IAppDialogService? dialogService = null)
    {
        var resolver = new DownloadContentConflictResolver(new UnexpectedDialogService());
        return new ContentDownloadCoordinator(
            factory,
            infoServiceFactory,
            new DownloadActionPlanner(resolver),
            dialogService ?? new UnexpectedDialogService(),
            NullLogger<ContentDownloadCoordinator>.Instance);
    }

    private sealed class UnexpectedDialogService : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(
                $"Unexpected dialog: {request.Dialog}.");
    }

    private sealed class RecordingUnavailableDialogService(
        DownloadUnavailableBatchAction action) : IAppDialogService
    {
        public List<DownloadUnavailableBatchPrompt> Prompts { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AppDialog.DownloadUnavailableBatch, request.Dialog);
            var prompt = Assert.IsType<DownloadUnavailableBatchPrompt>(
                request.Parameters![DownloadUnavailableBatchDialogContract.PromptParameter]);
            Prompts.Add(prompt);
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Accepted,
                DownloadUnavailableBatchDialogContract.Encode(action)));
        }
    }

    private sealed class RecordingInfoServiceFactory(
        Func<ContentDownloadItem, Exception?>? failure = null) : IContentInfoServiceFactory
    {
        public List<Type> CreatedInputTypes { get; } = [];

        public List<string> CreatedSources { get; } = [];

        public Task<IInfoService> CreateAsync(
            ContentDownloadItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedInputTypes.Add(item.Input.GetType());
            CreatedSources.Add(SourceOf(item));
            if (failure?.Invoke(item) is { } exception)
            {
                return Task.FromException<IInfoService>(exception);
            }

            return Task.FromResult<IInfoService>(new RecordingInfoService());
        }
    }

    private static ContentDownloadItem Item(
        string source,
        bool isSelected = true,
        string? displayName = null)
    {
        return ContentDownloadItem.TryCreate(source, isSelected, displayName)
            ?? throw new InvalidOperationException($"Invalid test input: {source}");
    }

    private static string SourceOf(ContentDownloadItem item)
    {
        return item.Input switch
        {
            BilibiliBvidInput bvid => bvid.Bvid,
            BilibiliAvidInput avid => $"av{avid.Avid}",
            _ => item.Input.CanonicalUri.AbsoluteUri
        };
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
