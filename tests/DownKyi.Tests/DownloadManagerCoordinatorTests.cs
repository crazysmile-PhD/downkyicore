using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadManagerCoordinatorTests
{
    [Fact]
    public async Task PauseAndResumeAllArePersistedForNextLaunch()
    {
        using var context = new CoordinatorContext();
        var item = context.CreateDownloadingItem("pause-resume", DownloadStatus.WaitForDownload);
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        await context.StateWriter.StartAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken);

        await context.Coordinator.PauseAllAsync(
            context.State.Downloading,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStatus.PauseStarted, item.Downloading.DownloadStatus);
        var paused = await context.Store.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken);
        Assert.Equal(DownloadPhase.Pausing, Assert.IsType<DownloadTask>(paused).Phase);

        await context.Coordinator.ResumeAllAsync(
            context.State.Downloading,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStatus.WaitForDownload, item.Downloading.DownloadStatus);
        var resumed = await context.Store.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken);
        Assert.Equal(DownloadPhase.Queued, Assert.IsType<DownloadTask>(resumed).Phase);
        Assert.Equal(
            item.DownloadBase.Id,
            Assert.Single(context.Queue.Enqueued).Value);
    }

    [Fact]
    public async Task RuntimeFailureDuringResumeDoesNotLeaveTaskQueued()
    {
        using var context = new CoordinatorContext(new RuntimeUnavailableTaskQueue());
        var item = context.CreateDownloadingItem("resume-runtime-failure", DownloadStatus.WaitForDownload);
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var taskId = new DownloadTaskId(item.DownloadBase.Id);
        await context.StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
        await context.StateWriter.FailAsync(
            taskId,
            new DownloadFailure("download.synthetic", "Synthetic failure.", true),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() =>
            context.Coordinator.ToggleAsync(item, TestContext.Current.CancellationToken));

        var persisted = Assert.IsType<DownloadTask>(
            await context.Store.FindAsync(taskId, TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Failed, persisted.Phase);
        Assert.Equal("download.runtime.unavailable", persisted.Failure?.Code);
    }

    [Fact]
    public async Task UnavailableRuntimeRejectsResumeBeforeStateTransition()
    {
        using var context = new CoordinatorContext(
            runtimeAvailability: new UnavailableDownloadRuntimeAvailability());
        var item = context.CreateDownloadingItem(
            "resume-known-runtime-failure",
            DownloadStatus.WaitForDownload);
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var taskId = new DownloadTaskId(item.DownloadBase.Id);
        await context.StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
        await context.StateWriter.FailAsync(
            taskId,
            new DownloadFailure("download.synthetic", "Synthetic failure.", true),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() =>
            context.Coordinator.ToggleAsync(item, TestContext.Current.CancellationToken));

        var persisted = Assert.IsType<DownloadTask>(
            await context.Store.FindAsync(taskId, TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Failed, persisted.Phase);
        Assert.Equal("download.synthetic", persisted.Failure?.Code);
    }

    [Fact]
    public async Task DeleteCanBeRetriedAfterAFormerFileCleanupLeftTaskCanceled()
    {
        using var context = new CoordinatorContext();
        var item = context.CreateDownloadingItem("delete-retry", DownloadStatus.WaitForDownload);
        item.Downloading.DownloadFiles["video"] = "delete-retry.mp4";
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        await context.StateWriter.CancelAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken);
        var media = context.CreateFile("delete-retry.mp4", "partial media");

        await context.Coordinator.DeleteAsync(item, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(media));
        Assert.DoesNotContain(item, context.State.Downloading);
        Assert.Equal(
            item.DownloadBase.Id,
            Assert.Single(context.Queue.Canceled).Value);
        Assert.Null(await context.Store.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRemovesGeneratedFilesResumeSidecarsStoreRowAndProjection()
    {
        using var context = new CoordinatorContext();
        var item = context.CreateDownloadingItem("delete-complete", DownloadStatus.WaitForDownload);
        item.DownloadBase.NeedDownloadContent = DownloadContentSelection.None;
        item.Downloading.DownloadFiles["video"] = "delete-complete.mp4";
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var media = context.CreateFile("delete-complete.mp4", "partial media");
        var sidecar = context.CreateFile("delete-complete.mp4.aria2", "resume state");

        await context.Coordinator.DeleteAsync(item, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(media));
        Assert.True(File.Exists(sidecar));
        Assert.DoesNotContain(item, context.State.Downloading);
        Assert.Null(await context.Store.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationBeforeDeletePreservesFilesStoreRowAndProjection()
    {
        using var context = new CoordinatorContext();
        var item = context.CreateDownloadingItem("delete-canceled", DownloadStatus.WaitForDownload);
        item.Downloading.DownloadFiles["video"] = "delete-canceled.mp4";
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var media = context.CreateFile("delete-canceled.mp4", "partial media");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Coordinator.DeleteAsync(item, cancellation.Token));

        Assert.True(File.Exists(media));
        Assert.Contains(item, context.State.Downloading);
        Assert.NotNull(await context.Store.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenVideoUsesRecordedPathWithoutGuessingOtherFiles()
    {
        using var context = new CoordinatorContext();
        var item = await context.CreateCompletedItemAsync("open-flv", "media", "open-flv.flv");
        var flv = context.CreateFile("open-flv.flv", "completed media");

        var result = await context.Coordinator.OpenVideoAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadArtifactOpenResult.Opened, result);
        Assert.Equal(Path.GetFullPath(flv), context.Launcher.OpenedFile);
    }

    [Fact]
    public async Task OpenFolderUsesPublishedPathWithoutGuessingFromOtherFiles()
    {
        using var context = new CoordinatorContext();
        var item = await context.CreateCompletedItemAsync(
            "open-subtitle", "subtitle:open-subtitle.srt", "open-subtitle.srt");
        context.CreateFile("open-subtitle.mp4", "unrequested media");

        var withoutSubtitle = await context.Coordinator.OpenFolderAsync(
            item,
            TestContext.Current.CancellationToken);
        var withoutMedia = await context.Coordinator.OpenVideoAsync(
            item,
            TestContext.Current.CancellationToken);
        var subtitle = context.CreateFile("open-subtitle.srt", "requested subtitle");
        var withSubtitle = await context.Coordinator.OpenFolderAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadArtifactOpenResult.NotFound, withoutSubtitle);
        Assert.Equal(DownloadArtifactOpenResult.NotFound, withoutMedia);
        Assert.Equal(DownloadArtifactOpenResult.Opened, withSubtitle);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(subtitle)), context.Launcher.OpenedFolder);
    }

    [Fact]
    public async Task ReopenedCompletedItemOpensPersistedPublishedPathNotBasePathGuess()
    {
        using var context = new CoordinatorContext();
        Directory.CreateDirectory(Path.Combine(context.DirectoryPath, "published"));
        var item = await context.CreateCompletedItemAsync(
            "reopen-open", "media", Path.Combine("published", "actual.flv"));
        var published = context.CreateFile(Path.Combine("published", "actual.flv"), "completed media");
        context.CreateFile("reopen-open.mp4", "decoy");
        context.ReopenStorage();
        var reopenedItem = Assert.Single(await context.Storage.GetRecentDownloadedAsync(
            10, TestContext.Current.CancellationToken));

        var fileResult = await context.Coordinator.OpenVideoAsync(
            reopenedItem, TestContext.Current.CancellationToken);
        var folderResult = await context.Coordinator.OpenFolderAsync(
            reopenedItem, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadArtifactOpenResult.Opened, fileResult);
        Assert.Equal(DownloadArtifactOpenResult.Opened, folderResult);
        Assert.Equal(Path.GetFullPath(published), context.Launcher.OpenedFile);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(published)), context.Launcher.OpenedFolder);
        Assert.NotEqual(item.DownloadBase?.FilePath + ".mp4", context.Launcher.OpenedFile);
    }

    [Fact]
    public async Task LegacyCompletedItemWithoutPublishedMapReportsMissingRecordWithoutGuessing()
    {
        using var context = new CoordinatorContext();
        var downloading = context.CreateDownloadingItem("legacy-no-map", DownloadStatus.WaitForDownload);
        await context.Storage.AddDownloadingAsync(downloading, TestContext.Current.CancellationToken);
        var taskId = new DownloadTaskId(downloading.DownloadBase!.Id);
        await context.StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
        await context.StateWriter.CompleteAsync(
            taskId, new DownloadCompletion(1, "completed", null), TestContext.Current.CancellationToken);
        var legacy = Assert.Single(await context.Storage.GetRecentDownloadedAsync(
            10, TestContext.Current.CancellationToken));
        context.CreateFile("legacy-no-map.mp4", "unowned decoy");
        context.CreateFile("legacy-no-map.flv", "unowned decoy");
        context.ReopenStorage();
        var reopened = Assert.Single(await context.Storage.GetRecentDownloadedAsync(
            10, TestContext.Current.CancellationToken));

        var file = await context.Coordinator.OpenVideoAsync(reopened, TestContext.Current.CancellationToken);
        var folder = await context.Coordinator.OpenFolderAsync(reopened, TestContext.Current.CancellationToken);

        Assert.Equal(legacy.DownloadBase?.Id, reopened.DownloadBase?.Id);
        Assert.Equal(DownloadArtifactOpenResult.NoPublishedArtifactRecord, file);
        Assert.Equal(DownloadArtifactOpenResult.NoPublishedArtifactRecord, folder);
        Assert.Null(context.Launcher.OpenedFile);
        Assert.Null(context.Launcher.OpenedFolder);
    }

    private sealed class CoordinatorContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-download-manager-tests",
            Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;

        public CoordinatorContext(
            IDownloadTaskQueue? taskQueue = null,
            IDownloadRuntimeAvailability? runtimeAvailability = null)
        {
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "download.db");
            Store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(_databasePath),
                new SystemClock());
            var clock = new SystemClock();
            TaskService = new DownloadTaskApplicationService(Store, clock);
            Storage = new DownloadTaskProjectionStore(TaskService, clock);
            StateWriter = new DownloadTaskStateWriter(TaskService);
            Queue = new RecordingDownloadTaskQueue();
            State = new DownloadListState();
            Launcher = new RecordingPlatformLauncher();
            var fileService = new DownloadTaskFileService(
                new AriaRuntimeClientRegistry(),
                NullLogger<DownloadTaskFileService>.Instance);
            Coordinator = new DownloadManagerCoordinator(
                Storage,
                StateWriter,
                taskQueue ?? Queue,
                runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
                fileService,
                State,
                Launcher);
        }

        public SqliteDownloadTaskStore Store { get; private set; }

        public DownloadTaskProjectionStore Storage { get; private set; }

        public DownloadTaskApplicationService TaskService { get; private set; }

        public DownloadTaskStateWriter StateWriter { get; private set; }

        public RecordingDownloadTaskQueue Queue { get; }

        public DownloadListState State { get; }

        public RecordingPlatformLauncher Launcher { get; }

        public DownloadManagerCoordinator Coordinator { get; private set; }

        public string DirectoryPath => _directory;

        public void ReopenStorage()
        {
            Storage.Dispose();
            TaskService.Dispose();
            Store.Dispose();
            Store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(_databasePath), new SystemClock());
            var clock = new SystemClock();
            TaskService = new DownloadTaskApplicationService(Store, clock);
            Storage = new DownloadTaskProjectionStore(TaskService, clock);
            StateWriter = new DownloadTaskStateWriter(TaskService);
            Coordinator = new DownloadManagerCoordinator(
                Storage, StateWriter, Queue, new ReadyDownloadRuntimeAvailability(),
                new DownloadTaskFileService(
                    new AriaRuntimeClientRegistry(), NullLogger<DownloadTaskFileService>.Instance),
                State, Launcher);
        }

        public DownloadingItem CreateDownloadingItem(string id, DownloadStatus status)
        {
            return new DownloadingItem
            {
                DownloadBase = new DownloadBase
                {
                    Id = id,
                    Name = id,
                    FilePath = Path.Combine(_directory, id)
                },
                Downloading = new Downloading
                {
                    Id = id,
                    DownloadStatus = status
                },
                PlayUrl = new PlayUrl()
            };
        }

        public async Task<DownloadedItem> CreateCompletedItemAsync(string id, string key, string fileName)
        {
            var downloading = CreateDownloadingItem(id, DownloadStatus.WaitForDownload);
            await Storage.AddDownloadingAsync(downloading, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var taskId = new DownloadTaskId(id);
            await StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await StateWriter.RecordPublishedArtifactAsync(
                taskId, key, Path.Combine(_directory, fileName), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await StateWriter.CompleteAsync(taskId,
                new DownloadCompletion(1, "completed", null), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            return Assert.Single(await Storage.GetRecentDownloadedAsync(
                10, TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        public string CreateFile(string name, string contents)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Storage.Dispose();
            TaskService.Dispose();
            Store.Dispose();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class RecordingPlatformLauncher : IPlatformLauncher
    {
        public string? OpenedFile { get; private set; }

        public string? OpenedFolder { get; private set; }

        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedFile = path;
            return Task.FromResult(true);
        }

        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedFolder = path;
            return Task.FromResult(true);
        }

        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class RuntimeUnavailableTaskQueue : IDownloadTaskQueue
    {
        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DownloadRuntimeUnavailableException(
                "Synthetic unavailable download runtime.");
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId)
        {
            return Task.FromResult(false);
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
}
