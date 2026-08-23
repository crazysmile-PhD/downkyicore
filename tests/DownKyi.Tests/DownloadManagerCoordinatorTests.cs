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

        Assert.False(File.Exists(media));
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
        item.Downloading.DownloadFiles["video"] = "delete-complete.mp4";
        context.State.AddDownloading(item);
        await context.Storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        var media = context.CreateFile("delete-complete.mp4", "partial media");
        var sidecar = context.CreateFile("delete-complete.mp4.aria2", "resume state");

        await context.Coordinator.DeleteAsync(item, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(media));
        Assert.False(File.Exists(sidecar));
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
    public async Task OpenVideoFallsBackToExistingFlvWithoutExposingPathToViewModel()
    {
        using var context = new CoordinatorContext();
        var item = context.CreateDownloadedItem("open-flv");
        var flv = context.CreateFile("open-flv.flv", "completed media");

        var result = await context.Coordinator.OpenVideoAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.Equal(DownloadArtifactOpenResult.Opened, result);
        Assert.Equal(Path.GetFullPath(flv), context.Launcher.OpenedFile);
    }

    private sealed class CoordinatorContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-download-manager-tests",
            Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;

        public CoordinatorContext()
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
                Queue,
                fileService,
                State,
                Launcher);
        }

        public SqliteDownloadTaskStore Store { get; }

        public DownloadTaskProjectionStore Storage { get; }

        public DownloadTaskApplicationService TaskService { get; }

        public DownloadTaskStateWriter StateWriter { get; }

        public RecordingDownloadTaskQueue Queue { get; }

        public DownloadListState State { get; }

        public RecordingPlatformLauncher Launcher { get; }

        public DownloadManagerCoordinator Coordinator { get; }

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

        public DownloadedItem CreateDownloadedItem(string id)
        {
            return new DownloadedItem
            {
                DownloadBase = new DownloadBase
                {
                    Id = id,
                    Name = id,
                    FilePath = Path.Combine(_directory, id)
                },
                Downloaded = new Downloaded { Id = id }
            };
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

        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedFile = path;
            return Task.FromResult(true);
        }

        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
