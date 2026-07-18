using System.Collections.Concurrent;
using System.Net.Http;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;
using CoreVideoPage = DownKyi.Core.BiliApi.Video.Models.VideoPage;
using VideoPage = DownKyi.ViewModels.PageViewModels.VideoPage;

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
        var service = new VideoInfoService(videoView, settings, provider, new TestWbiKeyProvider());
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
        context.Prepare(page);

        var added = await context.Service
            .AddToDownload(_directory, cancellationToken: operation.Token)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(operation.Token, observedToken);
        Assert.Equal("current", Assert.Single(Assert.Single(context.ListState.Downloading).Metadata!.Tags));
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
        context.Prepare(page);

        var addTask = context.Service.AddToDownload(_directory, cancellationToken: operation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await operation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
        Assert.Empty(context.ListState.Downloading);
        Assert.Equal(0, context.Store.AddCount);
    }

    [Fact]
    public async Task TagNetworkFailureDoesNotBlockDownloadTaskCreation()
    {
        using var context = CreateContext(generateMetadata: true);
        var page = CreatePage(_ => Task.FromException<IReadOnlyList<string>>(
            new HttpRequestException("tag endpoint unavailable")));
        context.Prepare(page);

        var added = await context.Service
            .AddToDownload(_directory, cancellationToken: TestContext.Current.CancellationToken)
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
        context.Prepare(CreatePage(_ => Task.FromResult<IReadOnlyList<string>>(["one", "two"])));

        var added = await context.Service
            .AddToDownload(_directory, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(["one", "two"], Assert.Single(context.ListState.Downloading).Metadata!.Tags);
    }

    [Fact]
    public async Task DisabledMovieMetadataDoesNotLoadTags()
    {
        using var context = CreateContext(generateMetadata: false);
        var loadCount = 0;
        context.Prepare(CreatePage(_ =>
        {
            loadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["unused"]);
        }));

        var added = await context.Service
            .AddToDownload(_directory, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(0, loadCount);
        Assert.Null(Assert.Single(context.ListState.Downloading).Metadata);
    }

    private DownloadTestContext CreateContext(bool generateMetadata)
    {
        Directory.CreateDirectory(_directory);
        return new DownloadTestContext(
            Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"),
            generateMetadata);
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
        private readonly DownloadTaskProjectionStore _projectionStore;

        public DownloadTestContext(string settingsPath, bool generateMetadata)
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
            _projectionStore = new DownloadTaskProjectionStore(Store, new SystemClock());
            ListState = new DownloadListState();
            Logger = new RecordingLogger<AddToDownloadService>();
            var desktop = new TestDesktopInteractionContext();
            Service = new AddToDownloadService(
                DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video,
                ListState,
                _projectionStore,
                _settings,
                new VideoTagProvider(),
                new TestWbiKeyProvider(),
                desktop.Notifications,
                desktop.Dialogs,
                Logger);
        }

        public AddToDownloadService Service { get; }

        public DownloadListState ListState { get; }

        public RecordingDownloadTaskStore Store { get; }

        public RecordingLogger<AddToDownloadService> Logger { get; }

        public void Prepare(VideoPage page)
        {
            Service.GetVideo(
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
                        VideoPages = [page]
                    }
                ]);
        }

        public void Dispose()
        {
            _projectionStore.Dispose();
            _settings.Dispose();
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

    private sealed class RecordingDownloadTaskStore : IDownloadTaskStore
    {
        public int AddCount { get; private set; }

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult<DownloadTask?>(null);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) => Task.FromResult(
                new DownloadHistoryPage(Array.Empty<DownloadTask>(), null));

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadTask>>(
                Array.Empty<DownloadTask>());

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
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
