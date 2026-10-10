using System.Security.Cryptography;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadManagerCoordinatorTests
{
    [Fact]
    public async Task PauseAllDelegatesIntentWithoutMutatingQueuedItems()
    {
        using var context = new CoordinatorContext();
        var queued = context.CreateDownloadingItem("pause-queued", DownloadStatus.WaitForDownload);
        context.State.AddDownloading(queued);
        await context.Storage.AddDownloadingAsync(queued, TestContext.Current.CancellationToken);

        await context.Coordinator.PauseAllAsync(
            context.State.Downloading,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, context.Scheduler.PauseCount);
        Assert.Equal(0, context.Scheduler.ResumeCount);
        Assert.Equal(DownloadPhase.Queued, (await context.GetTaskAsync(queued)).Phase);
        Assert.Empty(context.Queue.Enqueued);
    }

    [Fact]
    public async Task ResumeAllDelegatesIntentWithoutMutatingQueuedItems()
    {
        using var context = new CoordinatorContext();
        var queued = context.CreateDownloadingItem("resume-queued", DownloadStatus.WaitForDownload);
        context.State.AddDownloading(queued);
        await context.Storage.AddDownloadingAsync(queued, TestContext.Current.CancellationToken);

        await context.Coordinator.ResumeAllAsync(
            context.State.Downloading,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, context.Scheduler.PauseCount);
        Assert.Equal(1, context.Scheduler.ResumeCount);
        Assert.Equal(DownloadPhase.Queued, (await context.GetTaskAsync(queued)).Phase);
        Assert.Empty(context.Queue.Enqueued);
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
    public async Task DeleteAfterPublishCollisionRemovesOnlyStagingAndTask()
    {
        using var context = new CoordinatorContext(useStaging: true);
        var item = context.CreateDownloadingItem("delete-publish-collision", DownloadStatus.WaitForDownload);
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var taskId = new DownloadTaskId(item.DownloadBase.Id);
        var task = await context.StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
        var staged = Path.Combine(context.Staging!.GetDirectory(
            taskId, task.Output.BasePath, task.Output.StagingToken), "delete-publish-collision.mp4");
        var stagedBytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(staged, stagedBytes, TestContext.Current.CancellationToken);
        var destination = context.CreateFile("delete-publish-collision.mp4", "foreign output");
        var foreignBytes = await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken);
        await context.StateWriter.BeginPublishingArtifactAsync(taskId,
            new DownloadPublishingArtifact("media", Path.GetFileName(staged), stagedBytes.Length,
                Convert.ToHexString(SHA256.HashData(stagedBytes))),
            TestContext.Current.CancellationToken);
        await context.StateWriter.FailAsync(taskId,
            new DownloadFailure("download.publish.collision", "Destination exists.", true),
            TestContext.Current.CancellationToken);

        await context.Coordinator.DeleteAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(staged));
        Assert.Null(await context.Store.FindAsync(taskId, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(item, context.State.Downloading);
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
        var page = await context.Storage.GetDownloadedPageAsync(
            null, 10, TestContext.Current.CancellationToken);
        var reopenedItem = DownloadTaskProjectionMapper.ToDownloadedItem(Assert.Single(page.Items));

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
    public async Task RemovedCompletedHistoryIsImmediatelyEligibleForDownloadAgain()
    {
        using var context = new CoordinatorContext();
        await context.CreateCompletedItemAsync(
            "redownload-after-delete", "media", "redownload-after-delete.mp4");
        context.CreateFile("redownload-after-delete.mp4", "completed media");
        await context.Coordinator.LoadDownloadedHistoryAsync();
        var completed = Assert.Single(context.State.Downloaded);
        var desktop = new TestDesktopInteractionContext();
        var duplicatePolicy = new DownloadDuplicatePolicy(
            context.State,
            context.Storage,
            desktop.Dialogs);
        completed.DownloadBase.FilePath = Path.ChangeExtension(
            completed.HistoryRecord!.PublishedArtifacts["media"],
            null);
        var requested = new DownloadingItem
        {
            DownloadBase = completed.DownloadBase,
            Downloading = new Downloading
            {
                DownloadStatus = DownloadStatus.NotStarted,
                PlayStreamType = DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video
            }
        };
        requested.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Video = true
        };

        Assert.True(await duplicatePolicy.ShouldSkipAsync(
            requested,
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));

        await context.Coordinator.RemoveDownloadedAsync(
            completed,
            TestContext.Current.CancellationToken);

        Assert.Empty(context.State.Downloaded);
        Assert.Empty(await context.Storage.GetDownloadedAsync(
            TestContext.Current.CancellationToken));
        Assert.False(await duplicatePolicy.ShouldSkipAsync(
            requested,
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));
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
        var legacyPage = await context.Storage.GetDownloadedPageAsync(
            null, 10, TestContext.Current.CancellationToken);
        var legacy = DownloadTaskProjectionMapper.ToDownloadedItem(Assert.Single(legacyPage.Items));
        context.CreateFile("legacy-no-map.mp4", "unowned decoy");
        context.CreateFile("legacy-no-map.flv", "unowned decoy");
        context.ReopenStorage();
        var reopenedPage = await context.Storage.GetDownloadedPageAsync(
            null, 10, TestContext.Current.CancellationToken);
        var reopened = DownloadTaskProjectionMapper.ToDownloadedItem(Assert.Single(reopenedPage.Items));

        var file = await context.Coordinator.OpenVideoAsync(reopened, TestContext.Current.CancellationToken);
        var folder = await context.Coordinator.OpenFolderAsync(reopened, TestContext.Current.CancellationToken);

        Assert.Equal(legacy.DownloadBase?.Id, reopened.DownloadBase?.Id);
        Assert.Equal(DownloadArtifactOpenResult.NoPublishedArtifactRecord, file);
        Assert.Equal(DownloadArtifactOpenResult.NoPublishedArtifactRecord, folder);
        Assert.Null(context.Launcher.OpenedFile);
        Assert.Null(context.Launcher.OpenedFolder);
    }

    [Fact]
    public async Task CompleteHistoryLoadMergesCurrentItemsAndDoesNotReadAgain()
    {
        using var context = new CoordinatorContext();
        var first = await context.CreateCompletedItemAsync(
            "history-first", "media", "history-first.mp4");
        context.State.AddDownloaded(first);
        await context.CreateCompletedItemAsync(
            "history-second", "media", "history-second.mp4");

        await context.Coordinator.LoadDownloadedHistoryAsync();
        context.State.AddDownloaded(first);

        Assert.True(context.State.IsDownloadedHistoryLoaded);
        Assert.Equal(2, context.State.Downloaded.Count);
        Assert.Equal(
            ["history-first", "history-second"],
            context.State.Downloaded
                .Select(item => item.HistoryRecord.Id.Value)
                .Order(StringComparer.Ordinal));

        await context.CreateCompletedItemAsync(
            "history-after-load", "media", "history-after-load.mp4");
        await context.Coordinator.LoadDownloadedHistoryAsync();

        Assert.DoesNotContain(
            context.State.Downloaded,
            item => item.HistoryRecord.Id == new DownloadTaskId("history-after-load"));
    }

    [Fact]
    public async Task CompleteHistoryLoadStopsBeforeUiMutationDuringShutdown()
    {
        using var context = new CoordinatorContext();
        await context.CreateCompletedItemAsync(
            "history-during-shutdown", "media", "history-during-shutdown.mp4");
        await context.ApplicationCancellation.RequestShutdownAsync();

        await context.Coordinator.LoadDownloadedHistoryAsync();

        Assert.False(context.State.IsDownloadedHistoryLoaded);
        Assert.Empty(context.State.Downloaded);
    }

    [Fact]
    public async Task CoordinatorDisposalWaitsOnlyForTheBackgroundHistoryRead()
    {
        var dispatcher = new BlockingUiDispatcher();
        using var context = new CoordinatorContext(uiDispatcher: dispatcher);
        await context.CreateCompletedItemAsync(
            "history-before-shutdown", "media", "history-before-shutdown.mp4");

        var load = context.Coordinator.LoadDownloadedHistoryAsync();
        await dispatcher.Invoked.WaitAsync(TestContext.Current.CancellationToken);
        await context.ApplicationCancellation.RequestShutdownAsync();

        await context.Coordinator.DisposeAsync().AsTask()
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(load.IsCompleted);

        dispatcher.Release();
        await load.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(context.State.IsDownloadedHistoryLoaded);
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
            IDownloadRuntimeAvailability? runtimeAvailability = null,
            IDownloadSchedulerControl? schedulerControl = null,
            bool useStaging = false,
            IUiDispatcher? uiDispatcher = null)
        {
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "download.db");
            Store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(_databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(Store);
            TaskService = new DownloadTaskApplicationService(
                Store,
                historyService,
                clock);
            Storage = new DownloadTaskProjectionStore(
                TaskService,
                historyService,
                clock);
            StateWriter = new DownloadTaskStateWriter(TaskService);
            Queue = new RecordingDownloadTaskQueue();
            Scheduler = new RecordingSchedulerControl();
            State = new DownloadListState();
            ApplicationCancellation = new ApplicationCancellation();
            Launcher = new RecordingPlatformLauncher();
            Staging = useStaging
                ? new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance)
                : null;
            var fileService = new DownloadTaskFileService(
                new AriaRuntimeClientRegistry(),
                NullLogger<DownloadTaskFileService>.Instance, Staging, StateWriter);
            Coordinator = new DownloadManagerCoordinator(
                ApplicationCancellation,
                Storage,
                StateWriter,
                taskQueue ?? Queue,
                schedulerControl ?? Scheduler,
                runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
                fileService,
                State,
                Launcher,
                uiDispatcher ?? new ImmediateUiDispatcher());
        }

        public SqliteDownloadTaskStore Store { get; private set; }

        public ApplicationCancellation ApplicationCancellation { get; }

        public DownloadTaskProjectionStore Storage { get; private set; }

        public DownloadTaskApplicationService TaskService { get; private set; }

        public DownloadTaskStateWriter StateWriter { get; private set; }

        public DownloadTaskStaging? Staging { get; }

        public RecordingDownloadTaskQueue Queue { get; }

        public RecordingSchedulerControl Scheduler { get; }

        public DownloadListState State { get; }

        public RecordingPlatformLauncher Launcher { get; }

        public DownloadManagerCoordinator Coordinator { get; private set; }

        public string DirectoryPath => _directory;

        public async Task<DownloadTask> GetTaskAsync(DownloadingItem item)
        {
            return Assert.IsType<DownloadTask>(await Store.FindAsync(
                new DownloadTaskId(item.DownloadBase.Id),
                TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        public void ReopenStorage()
        {
            Coordinator.Dispose();
            Storage.Dispose();
            TaskService.Dispose();
            Store.Dispose();
            Store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(_databasePath), new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(Store);
            TaskService = new DownloadTaskApplicationService(Store, historyService, clock);
            Storage = new DownloadTaskProjectionStore(
                TaskService,
                historyService,
                clock);
            StateWriter = new DownloadTaskStateWriter(TaskService);
            Coordinator = new DownloadManagerCoordinator(
                ApplicationCancellation, Storage, StateWriter, Queue,
                Scheduler,
                new ReadyDownloadRuntimeAvailability(),
                new DownloadTaskFileService(
                    new AriaRuntimeClientRegistry(), NullLogger<DownloadTaskFileService>.Instance),
                State, Launcher, new ImmediateUiDispatcher());
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
                }
            };
        }

        public async Task<DownloadedItem> CreateCompletedItemAsync(string id, string key, string fileName)
        {
            var downloading = CreateDownloadingItem(id, DownloadStatus.WaitForDownload);
            downloading.DownloadBase.NeedDownloadContent = key switch
            {
                "media" => DownloadContentSelection.None with { Video = true },
                var subtitle when subtitle.StartsWith("subtitle:", StringComparison.Ordinal) =>
                    DownloadContentSelection.None with { Subtitle = true },
                _ => downloading.DownloadBase.NeedDownloadContent
            };
            await Storage.AddDownloadingAsync(downloading, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var taskId = new DownloadTaskId(id);
            await StateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var publishing = new DownloadPublishingArtifact(key, Path.GetFileName(fileName), 0,
                new string('A', 64));
            await StateWriter.BeginPublishingArtifactAsync(taskId, publishing,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await StateWriter.RecordPublishedArtifactAsync(
                taskId, publishing, Path.Combine(_directory, fileName),
                TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await StateWriter.CompleteAsync(taskId,
                new DownloadCompletion(1, "completed", null), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var page = await Storage.GetDownloadedPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            return DownloadTaskProjectionMapper.ToDownloadedItem(
                Assert.Single(page.Items, item => item.Id == taskId));
        }

        public string CreateFile(string name, string contents)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            ApplicationCancellation.Dispose();
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

        private sealed class ImmediateUiDispatcher : IUiDispatcher
        {
            public Task InvokeAsync(Action action)
            {
                ArgumentNullException.ThrowIfNull(action);
                action();
                return Task.CompletedTask;
            }
        }
    }

    private sealed class RecordingSchedulerControl : IDownloadSchedulerControl
    {
        public int PauseCount { get; private set; }

        public int ResumeCount { get; private set; }

        public Task PauseAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingUiDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource _invoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _action;

        public Task Invoked => _invoked.Task;

        public async Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            _action = action;
            _invoked.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            _action();
        }

        public void Release() => _release.TrySetResult();
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
