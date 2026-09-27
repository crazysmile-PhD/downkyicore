using System.Collections.Concurrent;
using System.Net.Http;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using CoreVideoPage = DownKyi.Core.BiliApi.Video.Models.VideoPage;
using VideoPage = DownKyi.Presentation.VideoPage;

namespace DownKyi.Tests;

public sealed class VideoTagLoadingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-video-tag-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VideoPageUsesCurrentOperationAfterOriginalParseWasCanceled()
    {
        Directory.CreateDirectory(_directory);
        using var settings = new DownKyi.Core.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        using var parseCancellation = new CancellationTokenSource();
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var provider = new RecordingTagProvider(
            (_, _, cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["tag"]));
        var videoView = new DownKyi.Core.BiliApi.Video.Models.VideoView
        {
            Aid = 42,
            Bvid = "BV1test",
            Title = "video",
            Pages =
            [
                new CoreVideoPage
                {
                    Cid = 84,
                    Page = 1,
                    Part = "page"
                }
            ]
        };
        var service = new VideoInfoService(
            videoView,
            settings,
            provider,
            new TestWbiKeyProvider(),
            new TestBilibiliApiClient());
        var page = Assert.Single(service.GetVideoPages(parseCancellation.Token)!);

        await parseCancellation.CancelAsync();
        var tags = await page.LoadTagsAsync(downloadCancellation.Token).ConfigureAwait(true);

        Assert.Equal(["tag"], tags);
        Assert.Equal(downloadCancellation.Token, provider.LastToken);
        Assert.NotEqual(parseCancellation.Token, provider.LastToken);
    }

    [Fact]
    public async Task AddToDownloadPassesCurrentOperationTokenToMetadataLoader()
    {
        using var context = CreateContext(generateMetadata: true);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        CancellationToken observedToken = default;
        var page = CreatePage(cancellationToken =>
        {
            observedToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<string>>(["current"]);
        });
        var preparedDownload = await context.PrepareAsync(page);

        var added = await context.Service
            .AddToDownload(CreateSelection(_directory), preparedDownload, cancellationToken: operation.Token)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(operation.Token, observedToken);
        Assert.Equal("current", Assert.Single(Assert.Single(context.ListState.Downloading).Metadata!.Tags));
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task CancelingAddToDownloadCancelsTagRequestAndDoesNotCreateTask()
    {
        using var context = CreateContext(generateMetadata: true);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = CreatePage(async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(true);
            return Array.Empty<string>();
        });
        var preparedDownload = await context.PrepareAsync(page);

        var addTask = context.Service.AddToDownload(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: operation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await operation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
        Assert.Empty(context.ListState.Downloading);
        Assert.Equal(0, context.Store.AddCount);
    }

    [Fact]
    public async Task CancellationAfterPersistenceDoesNotStrandQueuedTask()
    {
        using var context = CreateContext(generateMetadata: false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Store.AfterAddAsync = operation.CancelAsync;
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        var added = await context.Service
            .AddToDownload(CreateSelection(_directory), preparedDownload, cancellationToken: operation.Token)
            .ConfigureAwait(true);

        Assert.True(operation.IsCancellationRequested);
        Assert.Equal(1, added);
        Assert.Single(context.ListState.Downloading);
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task TagNetworkFailureDoesNotBlockDownloadTaskCreation()
    {
        using var context = CreateContext(generateMetadata: true);
        var page = CreatePage(_ => Task.FromException<IReadOnlyList<string>>(
            new HttpRequestException("tag endpoint unavailable")));
        var preparedDownload = await context.PrepareAsync(page);

        var added = await context.Service
            .AddToDownload(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Empty(Assert.Single(context.ListState.Downloading).Metadata!.Tags);
        var warning = Assert.Single(context.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
    }

    [Fact]
    public async Task CanceledTagLoadIsNotPermanentlyCached()
    {
        var attempts = 0;
        var page = CreatePage(cancellationToken =>
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(["recovered"]);
        });
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => page.LoadTagsAsync(canceled.Token));
        var tags = await page.LoadTagsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, attempts);
        Assert.Equal(["recovered"], tags);
    }

    [Fact]
    public async Task EnabledMovieMetadataIncludesLoadedTags()
    {
        using var context = CreateContext(generateMetadata: true);
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>(["one", "two"])));

        var added = await context.Service
            .AddToDownload(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(["one", "two"], Assert.Single(context.ListState.Downloading).Metadata!.Tags);
    }

    [Fact]
    public async Task MalformedOptionalApiTagsDoNotBlockDownloadTaskCreation()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = static (_, _) => Task.FromResult(
                """
                {
                  "code": 0,
                  "data": [
                    { "tag_id": 1, "tag_name": null },
                    { "tag_id": 2, "tag_name": "" },
                    { "tag_id": 3, "tag_name": "   " },
                    { "tag_id": 4, "tag_name": "kept" }
                  ]
                }
                """)
        };
        var provider = new VideoTagProvider(client);
        using var context = CreateContext(generateMetadata: true);
        var preparedDownload = await context.PrepareAsync(CreatePage(cancellationToken => provider.GetTagsAsync(
            "BV1test",
            84,
            cancellationToken)));

        var added = await context.Service
            .AddToDownload(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(["kept"], Assert.Single(context.ListState.Downloading).Metadata!.Tags);
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task DisabledMovieMetadataDoesNotLoadTags()
    {
        using var context = CreateContext(generateMetadata: false);
        var loadCount = 0;
        var preparedDownload = await context.PrepareAsync(CreatePage(_ =>
        {
            loadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["unused"]);
        }));

        var added = await context.Service
            .AddToDownload(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(0, loadCount);
        Assert.Null(Assert.Single(context.ListState.Downloading).Metadata);
    }

    [Fact]
    public async Task ResolverFailureIsReportedAndDoesNotBlockNextAdmission()
    {
        var resolver = new FailFirstPhysicalOutputPathResolver();
        using var context = CreateContext(generateMetadata: false, resolver);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Cid = 1;
        first.Name = "first";
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Cid = 2;
        second.Name = "second";
        var preparedDownload = await context.PrepareAsync(first, second);

        var added = await context.Service
            .AddToDownload(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, context.Store.AddCount);
        Assert.Equal("second", Assert.Single(context.ListState.Downloading).DownloadBase.Name);
        Assert.Single(context.Queue.Enqueued);
        var request = Assert.Single(context.Dialogs.Requests);
        Assert.Equal(AppDialog.Alert, request.Dialog);
        var message = Assert.IsType<string>(request.Parameters!["message"]);
        Assert.Equal(DictionaryResource.GetString("DirectoryError"), message);
        Assert.DoesNotContain(_directory, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownResolverFailureKeepsExistingFailureBehavior()
    {
        using var context = CreateContext(
            generateMetadata: false,
            new UnknownFailurePhysicalOutputPathResolver());
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.AddToDownload(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, context.Store.AddCount);
        Assert.Empty(context.ListState.Downloading);
        Assert.Empty(context.Queue.Enqueued);
        Assert.Empty(context.Dialogs.Requests);
    }

    [Fact]
    public async Task RuntimeFailureStopsAdmissionAcrossRemainingSections()
    {
        using var context = CreateContext(
            generateMetadata: false,
            runtimeAvailability: new UnavailableDownloadRuntimeAvailability());
        var preparedDownload = await context.PrepareSectionsAsync(
            [CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]))],
            [CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]))]);

        var added = await context.Service.AddToDownload(
            CreateSelection(_directory),
            preparedDownload,
            isAll: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, added);
        Assert.Equal(0, context.Store.AddCount);
        Assert.Empty(context.ListState.Downloading);
        Assert.Single(context.Dialogs.Requests);
    }

    [Fact]
    public async Task MultiPageAdmissionReadsCompletedHistoryOnce()
    {
        using var context = CreateContext(generateMetadata: false);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Cid = 1;
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Cid = 2;
        var preparedDownload = await context.PrepareAsync(first, second);

        var added = await context.Service.AddToDownload(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, added);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task ExistingDataPreparationReportsEachPagesMediaCapabilities()
    {
        using var context = CreateContext(generateMetadata: false);
        var videoOnly = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        videoOnly.PlayUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo()]
            }
        };
        var audioAndVideo = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        audioAndVideo.PlayUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo()],
                Audio = [new PlayUrlDashVideo()]
            }
        };
        var combined = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        combined.PlayUrl = new PlayUrl
        {
            Durl = [new PlayUrlDurl()]
        };
        var dolby = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        dolby.PlayUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo()],
                Dolby = new PlayUrlDashDolby { Audio = [new PlayUrlDashVideo()] }
            }
        };
        var flac = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        flac.PlayUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo()],
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo { BaseAddress = "https://media.invalid/audio.flac" }
                }
            }
        };

        var preparedDownload = await context.PrepareAsync(
            videoOnly,
            audioAndVideo,
            combined,
            dolby,
            flac);
        var pages = Assert.Single(preparedDownload.Sections).Pages;

        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: false), pages[0].AvailableMedia);
        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: true), pages[1].AvailableMedia);
        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: true), pages[2].AvailableMedia);
        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: true), pages[3].AvailableMedia);
        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: true), pages[4].AvailableMedia);
    }

    [Fact]
    public async Task ExplicitRequestedContentIsStoredWithoutDirectorySelectionState()
    {
        using var context = CreateContext(generateMetadata: false);
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));
        var requestedContent = DownloadContentSelection.None with
        {
            Audio = true,
            Subtitle = true
        };

        var added = await context.Service.AddToDownload(
            CreateSelection(_directory, requestedContent),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, added);
        Assert.Equal(
            requestedContent,
            Assert.Single(context.ListState.Downloading).DownloadBase.NeedDownloadContent);
    }

    [Fact]
    public async Task InfoServicePreparationResolvesPlaybackIntoTheSamePreparedResult()
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlayUrl = null;
        var video = new VideoInfoView { Title = "resolved" };
        var section = new VideoSection { VideoPages = [page] };
        var infoService = new PreparationInfoService(
            video,
            [section],
            new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video = [new PlayUrlDashVideo()]
                }
            });

        var preparedDownload = await context.Service.PrepareAsync(
            infoService,
            TestContext.Current.CancellationToken);

        Assert.NotNull(preparedDownload);
        Assert.Same(video, preparedDownload.Video);
        var preparedPage = Assert.Single(Assert.Single(preparedDownload.Sections).Pages);
        Assert.Same(page, preparedPage.Page);
        Assert.Equal(new DownloadMediaCapabilities(Video: true, Audio: false), preparedPage.AvailableMedia);
        Assert.True(page.IsSelected);
        Assert.Equal(1, infoService.StreamRequestCount);
    }

    private DownloadTestContext CreateContext(
        bool generateMetadata,
        IPhysicalOutputPathResolver? resolver = null,
        IDownloadTaskQueue? taskQueue = null,
        IDownloadRuntimeAvailability? runtimeAvailability = null)
    {
        Directory.CreateDirectory(_directory);
        return new DownloadTestContext(
            Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"),
            generateMetadata,
            resolver,
            taskQueue,
            runtimeAvailability);
    }

    private static DownloadAddSelection CreateSelection(
        string directory,
        DownloadContentSelection? requestedContent = null)
    {
        return new DownloadAddSelection(
            directory,
            requestedContent ?? DownloadContentSelection.All);
    }

    private static VideoPage CreatePage(
        Func<CancellationToken, Task<IReadOnlyList<string>>> loadTagsAsync)
    {
        return new VideoPage
        {
            Avid = 42,
            Bvid = "BV1test",
            Cid = 84,
            EpisodeId = -1,
            IsSelected = true,
            Name = "page",
            Order = 1,
            OriginalPublishTime = new DateTime(2024, 1, 2),
            PublishTime = "2024-01-02",
            PlayUrl = new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrl(),
            VideoQuality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P",
                SelectedVideoCodec = "AVC"
            },
            LoadTagsAsync = loadTagsAsync
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DownloadTestContext : IDisposable
    {
        private readonly DownKyi.Core.Settings.SettingsStore _settings;
        private readonly DownloadTaskApplicationService _taskService;
        private readonly DownloadTaskProjectionStore _projectionStore;
        private readonly DownloadTaskAdmissionService _admission;

        public DownloadTestContext(
            string settingsPath,
            bool generateMetadata,
            IPhysicalOutputPathResolver? resolver,
            IDownloadTaskQueue? taskQueue,
            IDownloadRuntimeAvailability? runtimeAvailability)
        {
            _settings = new DownKyi.Core.Settings.SettingsStore(settingsPath);
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    Content = settings.Video.Content with
                    {
                        GenerateMovieMetadata = generateMetadata
                    }
                }
            });
            Store = new RecordingDownloadTaskStore();
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(Store);
            _taskService = new DownloadTaskApplicationService(Store, historyService, clock);
            _projectionStore = new DownloadTaskProjectionStore(
                _taskService,
                historyService,
                clock);
            ListState = new DownloadListState();
            Queue = new RecordingDownloadTaskQueue();
            Logger = new RecordingLogger<DownloadMovieMetadataBuilder>();
            Dialogs = new RecordingDialogService();
            var desktop = new TestDesktopInteractionContext();
            var client = new TestBilibiliApiClient();
            _admission = new DownloadTaskAdmissionService(
                ListState,
                _taskService,
                _projectionStore,
                new DownloadTaskStateWriter(_taskService),
                taskQueue ?? Queue,
                runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
                resolver ?? new FileSystemPhysicalOutputPathResolver());
            var duplicatePolicy = new DownloadDuplicatePolicy(
                ListState,
                _projectionStore,
                desktop.Notifications,
                Dialogs);
            Service = new AddToDownloadService(
                DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video,
                _admission,
                new LegacyDownloadAdmissionPresenter(_taskService, Dialogs),
                duplicatePolicy,
                new DownloadMovieMetadataBuilder(Logger),
                _settings,
                new VideoTagProvider(client),
                new TestWbiKeyProvider(),
                client,
                Dialogs,
                new RecordingLogger<AddToDownloadService>());
        }

        public AddToDownloadService Service { get; }

        public DownloadListState ListState { get; }

        public RecordingDownloadTaskStore Store { get; }

        public RecordingDownloadTaskQueue Queue { get; }

        public RecordingLogger<DownloadMovieMetadataBuilder> Logger { get; }

        public RecordingDialogService Dialogs { get; }

        public Task<PreparedDownload> PrepareAsync(params VideoPage[] pages)
        {
            return Service.PrepareAsync(
                new VideoInfoView
                {
                    Title = "video",
                    Description = "description",
                    VideoZone = "Technology"
                },
                [
                    new VideoSection
                    {
                        Id = 1,
                        IsSelected = true,
                        Title = "section",
                        VideoPages = pages
                    }
                ],
                isAll: false,
                TestContext.Current.CancellationToken);
        }

        public Task<PreparedDownload> PrepareSectionsAsync(params VideoPage[][] sections)
        {
            return Service.PrepareAsync(
                new VideoInfoView
                {
                    Title = "video",
                    Description = "description",
                    VideoZone = "Technology"
                },
                sections.Select((pages, index) => new VideoSection
                {
                    Id = index + 1,
                    IsSelected = true,
                    Title = $"section-{index + 1}",
                    VideoPages = pages
                }).ToArray(),
                isAll: true,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            _admission.Dispose();
            _projectionStore.Dispose();
            _taskService.Dispose();
            _settings.Dispose();
        }
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public List<AppDialogRequest> Requests { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class FailFirstPhysicalOutputPathResolver : IPhysicalOutputPathResolver
    {
        public int CallCount { get; private set; }

        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new IOException("Unable to resolve physical output path.");
            }

            return logicalBasePath;
        }
    }

    private sealed class UnknownFailurePhysicalOutputPathResolver : IPhysicalOutputPathResolver
    {
        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            throw new InvalidOperationException("Unexpected resolver failure.");
        }
    }

    private sealed class UnavailableDownloadRuntimeAvailability : IDownloadRuntimeAvailability
    {
        public void EnsureAcceptingTasks()
        {
            throw new DownloadRuntimeUnavailableException(
                "Synthetic unavailable download runtime.");
        }

        public Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadRuntimeStartupOutcome.Faulted(
                new DownloadRuntimeUnavailableException(
                    "Synthetic unavailable download runtime.")));
        }
    }

    private sealed class RecordingTagProvider(
        Func<string, long, CancellationToken, Task<IReadOnlyList<string>>> loadTagsAsync)
        : IVideoTagProvider
    {
        public CancellationToken LastToken { get; private set; }

        public Task<IReadOnlyList<string>> GetTagsAsync(
            string bvid,
            long cid,
            CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return loadTagsAsync(bvid, cid, cancellationToken);
        }
    }

    private sealed class PreparationInfoService(
        VideoInfoView video,
        IList<VideoSection> sections,
        PlayUrl playUrl) : IInfoService
    {
        public int StreamRequestCount { get; private set; }

        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return video;
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(noUgc);
            return sections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamRequestCount++;
            return Task.FromResult<PlayUrl?>(playUrl);
        }
    }

    private sealed class RecordingDownloadTaskStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        public int AddCount { get; private set; }

        public int HistoryPageRequestCount { get; private set; }

        public Func<Task>? AfterAddAsync { get; set; }

        public async Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            if (AfterAddAsync != null)
            {
                await AfterAddAsync().ConfigureAwait(false);
            }

            return OperationResult.Success();
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult<DownloadTask?>(null);

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            HistoryPageRequestCount++;
            return Task.FromResult(new DownloadHistoryPage([], null));
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadTask>>(
                Array.Empty<DownloadTask>());

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
