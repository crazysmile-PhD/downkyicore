using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Presentation;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadAddOwnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-download-add-owner-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ActiveDuplicateIsSkippedAndReported()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        context.ListState.AddDownloading(CreateDownloadingItem());

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreatePage(),
            CreateVideoQuality(),
            DownKyi.Core.Settings.RepeatDownloadStrategy.ReDownload,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Single(context.Notifications.Messages);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task CompletedDuplicateJumpOverPreservesHistory()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreatePage(),
            CreateVideoQuality(),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Single(context.ListState.Downloaded);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task CompletedDuplicateReDownloadAllowsNewTaskWithoutDeletingHistory()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Canceled);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreatePage(),
            CreateVideoQuality(),
            DownKyi.Core.Settings.RepeatDownloadStrategy.ReDownload,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
        Assert.Single(context.ListState.Downloaded);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task RejectedDuplicateConfirmationPreservesCompletedRecord()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Canceled);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreatePage(),
            CreateVideoQuality(),
            DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Single(context.ListState.Downloaded);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(1, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task AcceptedDuplicateConfirmationDeletesPersistedRecordBeforeAllowingTask()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreatePage(),
            CreateVideoQuality(),
            DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.Equal(1, context.Store.UpdateCount);
        Assert.Equal(DownloadPhase.Deleted, context.Store.Current?.Phase);
        Assert.Equal(1, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task DuplicatePolicyPropagatesCancellationBeforeInspectingLists()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Accepted);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Policy.ShouldSkipAsync(
                CreatePage(),
                CreateVideoQuality(),
                DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
                cancellation.Token));
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public void DraftFactoryPreservesTaskIdentityQualityContentAndStreamType()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.EpisodeId = 99;
        page.Page = 2;
        page.Duration = "01:23";
        page.FirstFrame = "frame";
        var video = new VideoInfoView
        {
            CoverUrl = "cover",
            Title = "main",
            TypeId = -10,
            VideoZone = "Technology>Software"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };
        var content = new DownloadContentSelection(
            Audio: true,
            Video: false,
            Danmaku: true,
            Subtitle: false,
            Cover: true);

        var item = DownloadTaskDraftFactory.Create(
            _directory,
            video,
            section,
            sectionCount: 2,
            page,
            CreateVideoQuality(),
            settingsStore.Current,
            content);

        Assert.Equal(page.Bvid, item.DownloadBase.Bvid);
        Assert.Equal(page.Avid, item.DownloadBase.Avid);
        Assert.Equal(page.Cid, item.DownloadBase.Cid);
        Assert.Equal(page.EpisodeId, item.DownloadBase.EpisodeId);
        Assert.Equal(page.Page, item.DownloadBase.Page);
        Assert.Equal(80, item.Resolution.Id);
        Assert.Equal("1080P", item.Resolution.Name);
        Assert.Equal("AVC", item.VideoCodecName);
        Assert.Equal(
            DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Cheese,
            item.Downloading.PlayStreamType);
        Assert.Equal(DownKyi.Models.DownloadStatus.NotStarted, item.Downloading.DownloadStatus);
        Assert.True(item.DownloadBase.NeedDownloadContent["downloadAudio"]);
        Assert.False(item.DownloadBase.NeedDownloadContent["downloadVideo"]);
        Assert.True(item.DownloadBase.NeedDownloadContent["downloadDanmaku"]);
        Assert.False(item.DownloadBase.NeedDownloadContent["downloadSubtitle"]);
        Assert.True(item.DownloadBase.NeedDownloadContent["downloadCover"]);
        Assert.StartsWith(_directory, item.DownloadBase.FilePath, StringComparison.Ordinal);
        Assert.Contains("section", item.DownloadBase.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftFactoryDefersCollisionResolutionToAtomicAdmission()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Basic = settingsStore.Current.Basic with
            {
                RepeatFileAutoAddNumberSuffix = true
            }
        };
        var page = CreatePage();
        var video = new VideoInfoView
        {
            Title = "main",
            VideoZone = "Technology"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };
        var first = DownloadTaskDraftFactory.Create(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(),
            settings,
            DownloadContentSelection.All);
        Directory.CreateDirectory(Path.GetDirectoryName(first.DownloadBase.FilePath)!);
        File.WriteAllText($"{first.DownloadBase.FilePath}.mp4", "occupied");

        var second = DownloadTaskDraftFactory.Create(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(),
            settings,
            DownloadContentSelection.All);

        Assert.Equal(first.DownloadBase.FilePath, second.DownloadBase.FilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static VideoPage CreatePage()
    {
        return new VideoPage
        {
            Avid = 42,
            Bvid = "BV1test",
            Cid = 84,
            IsSelected = true,
            Name = "page",
            Order = 1,
            OriginalPublishTime = new DateTime(2024, 1, 2),
            PublishTime = "2024-01-02",
            PlayUrl = new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrl(),
            VideoQuality = CreateVideoQuality()
        };
    }

    private static VideoQuality CreateVideoQuality()
    {
        return new VideoQuality
        {
            Quality = 80,
            QualityFormat = "1080P",
            SelectedVideoCodec = "AVC"
        };
    }

    private static DownloadingItem CreateDownloadingItem()
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = "duplicate-task",
                Avid = 42,
                Bvid = "BV1test",
                Cid = 84,
                FilePath = "video",
                Name = "page",
                Resolution = new DownKyi.Core.BiliApi.BiliUtils.Quality
                {
                    Id = 80,
                    Name = "1080P"
                },
                VideoCodecName = "AVC"
            },
            Downloading = new Downloading
            {
                DownloadStatus = DownKyi.Models.DownloadStatus.NotStarted,
                PlayStreamType = DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video
            },
            PlayUrl = new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrl()
        };
    }

    private sealed class DuplicatePolicyContext : IDisposable
    {
        private readonly DownloadTaskApplicationService _taskService;
        private readonly DownloadTaskProjectionStore _projectionStore;

        public DuplicatePolicyContext(
            AppDialogOutcome outcome,
            DownloadTask? current = null)
        {
            Store = new MutableDownloadTaskStore(current);
            _taskService = new DownloadTaskApplicationService(Store, new SystemClock());
            _projectionStore = new DownloadTaskProjectionStore(_taskService, new SystemClock());
            ListState = new DownloadListState();
            Notifications = new RecordingNotificationService();
            Dialogs = new StubDialogService(outcome);
            Policy = new DownloadDuplicatePolicy(
                ListState,
                _projectionStore,
                Notifications,
                Dialogs);
        }

        public DownloadDuplicatePolicy Policy { get; }

        public DownloadListState ListState { get; }

        public MutableDownloadTaskStore Store { get; }

        public RecordingNotificationService Notifications { get; }

        public StubDialogService Dialogs { get; }

        public static DuplicatePolicyContext WithCompleted(AppDialogOutcome outcome)
        {
            var queued = DownloadTaskProjectionMapper.CreateNewTask(
                CreateDownloadingItem(),
                DateTimeOffset.UnixEpoch);
            Assert.True(queued.Start(DateTimeOffset.UnixEpoch.AddSeconds(1))
                .TryGetValue(out var started));
            Assert.True(started.Complete(
                new DownloadCompletion(2, "finished", null),
                DateTimeOffset.UnixEpoch.AddSeconds(2))
                .TryGetValue(out var completed));
            var context = new DuplicatePolicyContext(outcome, completed);
            context.ListState.AddDownloaded(DownloadTaskProjectionMapper.ToDownloadedItem(completed));
            return context;
        }

        public void Dispose()
        {
            _projectionStore.Dispose();
            _taskService.Dispose();
        }
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class StubDialogService(AppDialogOutcome outcome) : IAppDialogService
    {
        public int ShowCount { get; private set; }

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowCount++;
            return Task.FromResult(new AppDialogResult(
                outcome,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class MutableDownloadTaskStore(DownloadTask? current) : IDownloadTaskStore
    {
        public DownloadTask? Current { get; private set; } = current;

        public int UpdateCount { get; private set; }

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current?.Id == taskId ? Current : null);
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage(Array.Empty<DownloadTask>(), null));

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current == null || Current.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(new OperationError(
                    "download.store.conflict",
                    "Version mismatch.",
                    OperationErrorKind.Conflict)));
            }

            Current = task;
            UpdateCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());
    }
}
