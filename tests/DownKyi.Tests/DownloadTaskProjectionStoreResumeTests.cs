using System.Text.Json;
using DownKyi.Application.Downloads;
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

public sealed class DownloadTaskProjectionStoreResumeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-storage-resume-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BlockedPresentationKeepsOnlyLatestProgressAndItsPeakSpeed()
    {
        using var fixture = new LiveProjectionFixture(_directory);
        await fixture.InitializeAsync();
        var notifications = 0;
        fixture.Item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadingItem.Progress)) notifications++;
        };
        var precedingInvocations = fixture.Dispatcher.Invocations;
        fixture.Dispatcher.Block();
        try
        {
            for (var sample = 1; sample <= 1000; sample++)
                fixture.Projections.PublishLiveProgress(fixture.Id, new DownloadProgress(
                    sample / 10d, sample, 1000, sample == 500 ? 9_000_000 : 1,
                    downloadedSizeText: sample == 1000 ? "latest" : "superseded"));
        }
        finally
        {
            fixture.Dispatcher.Release();
        }

        await fixture.Projections.FlushPresentationAsync();
        Assert.Equal(1, fixture.Dispatcher.Invocations - precedingInvocations);
        Assert.Equal(1, notifications);
        Assert.Equal(100, fixture.Item.Progress);
        Assert.Equal("latest", fixture.Item.DownloadingFileSize);
        Assert.Equal(9_000_000, fixture.Item.Downloading.MaxSpeed);
    }

    [Fact]
    public async Task BufferedLiveProgressCannotOverwriteCommittedPause()
    {
        using var fixture = new LiveProjectionFixture(_directory);
        await fixture.InitializeAsync();
        fixture.Dispatcher.Block();
        try
        {
            fixture.Projections.PublishLiveProgress(fixture.Id, new DownloadProgress(80, 80, 100));
            await fixture.Writer.PauseAsync(fixture.Id, TestContext.Current.CancellationToken);
            await fixture.Writer.UpdateProgressAsync(fixture.Id, new DownloadProgress(40, 40, 100),
                TestContext.Current.CancellationToken);
            await fixture.Writer.ConfirmPausedIfPausingAsync(fixture.Id, TestContext.Current.CancellationToken);
        }
        finally
        {
            fixture.Dispatcher.Release();
        }

        await fixture.Projections.FlushPresentationAsync();
        Assert.Equal(40, fixture.Item.Progress);
        Assert.Equal(DownloadStatus.Pause, fixture.Item.Downloading.DownloadStatus);
        Assert.Equal(DownloadPhase.Paused, fixture.Tasks.GetRequiredSnapshot(fixture.Id).Phase);
    }

    [Fact]
    public async Task BufferedLiveProgressSurvivesActivityOnlyCommit()
    {
        using var fixture = new LiveProjectionFixture(_directory);
        await fixture.InitializeAsync();
        fixture.Dispatcher.Block();
        try
        {
            fixture.Projections.PublishLiveProgress(fixture.Id, new DownloadProgress(60, 60, 100, 1000));
            await fixture.Writer.UpdateActivityAsync(fixture.Id, "video", "verifying",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            fixture.Dispatcher.Release();
        }

        await fixture.Projections.FlushPresentationAsync();
        Assert.Equal(60, fixture.Item.Progress);
        Assert.Equal(1000, fixture.Item.Downloading.MaxSpeed);
        Assert.Equal("verifying", fixture.Item.DownloadStatusTitle);
    }

    [Fact]
    public async Task TransferPausePreservesLiveProgressThroughConfirmationAndDatabaseReopen()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "transfer-pause.db");
        var item = new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = "transfer-pause",
                FilePath = Path.Combine(_directory, "episode")
            },
            Downloading = new Downloading
            {
                Id = "transfer-pause",
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
        using (var store = new SqliteDownloadTaskStore(new SqliteDownloadTaskStoreOptions(database), new SystemClock()))
        {
            var clock = new SystemClock();
            var history = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, history, clock);
            using var projections = new DownloadTaskProjectionStore(tasks, history);
            var writer = new DownloadTaskStateWriter(tasks);
            await projections.AddDownloadingAsync(tasks, item, TestContext.Current.CancellationToken);
            var id = new DownloadTaskId(item.DownloadBase.Id);
            await writer.StartAsync(id, TestContext.Current.CancellationToken);
            var backend = new PausingTransferBackend();
            var coordinator = new DownloadTransferCoordinator(backend, new DownloadRetryPolicy(),
                TimeProvider.System, NullLogger<DownloadTransferCoordinator>.Instance);
            var request = DownloadTransferRequestFactory.Create(id, ["https://media.invalid/file"],
                _directory, _directory, "media", 100, tasks,
                progress => projections.PublishLiveProgress(id, progress), writer, static () => { },
                TestContext.Current.CancellationToken);

            var transfer = coordinator.TransferAsync(request,
                static _ => throw new InvalidOperationException("A pause cannot refresh addresses."),
                TestContext.Current.CancellationToken);
            await backend.Started.Task.ConfigureAwait(true);
            Assert.Equal(60, item.Progress);
            Assert.Equal(0, tasks.GetRequiredSnapshot(id).Progress.Percentage);
            await writer.PauseAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(DownloadTransferOutcome.Paused, (await transfer.ConfigureAwait(true)).Outcome);
            Assert.True(backend.Stopped);
            await writer.ConfirmPausedIfPausingAsync(id, TestContext.Current.CancellationToken);

            Assert.Equal(DownloadPhase.Paused, tasks.GetRequiredSnapshot(id).Phase);
            Assert.Equal(60, tasks.GetRequiredSnapshot(id).Progress.DownloadedBytes);
            Assert.Equal(60, item.Progress);
            Assert.Equal("60 B/100 B", item.DownloadingFileSize);
        }

        using var reopenedStore = new SqliteDownloadTaskStore(new SqliteDownloadTaskStoreOptions(database), new SystemClock());
        var reopenedClock = new SystemClock();
        var reopenedHistory = DownloadHistoryService.CreateForSharedStore(reopenedStore);
        using var reopenedTasks = new DownloadTaskApplicationService(reopenedStore, reopenedHistory, reopenedClock);
        using var reopened = new DownloadTaskProjectionStore(reopenedTasks, reopenedHistory);
        var startup = await reopened.GetDownloadingStateAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(startup.Projections);
        Assert.Equal(DownloadStatus.Pause, restored.Downloading.DownloadStatus);
        Assert.Equal(60, restored.Progress);
        Assert.Equal("60 B/100 B", restored.DownloadingFileSize);
    }

    private sealed class PausingTransferBackend : ITransferBackend
    {
        public string Name => "pause-checkpoint";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }

        public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            request.PublishProgress(new DownloadProgress(60, 60, 100, downloadedSizeText: "60 B/100 B"));
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken).ConfigureAwait(false);
                return DownloadTransferResult.Succeeded();
            }
            finally
            {
                Stopped = true;
            }
        }
    }

    [Fact]
    public async Task AddDownloadingPreservesResumeIdentityFilesAndPausedStateAcrossReopen()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "download.db");
        const string taskId = "resume-task-01";
        const string ariaGid = "2089b05ecca3d829";
        var item = new DownloadingItem
        {
            Downloading = new Downloading
            {
                Id = taskId,
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
        item.DownloadBase.Id = taskId;
        item.DownloadBase.FilePath = Path.Combine(_directory, "episode-01");

        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        {
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var storage = new DownloadTaskProjectionStore(
                tasks,
                historyService);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await storage.AddDownloadingAsync(tasks, item, TestContext.Current.CancellationToken);
            var id = new DownloadTaskId(taskId);
            await stateWriter.StartAsync(id, TestContext.Current.CancellationToken);
            await stateWriter.RecordTransferFileAsync(
                id,
                "video",
                "video.m4s",
                TestContext.Current.CancellationToken);
            await stateWriter.RecordTransferFileAsync(
                id,
                "audio",
                "audio.m4s",
                TestContext.Current.CancellationToken);
            await stateWriter.CompleteTransferFileAsync(
                id,
                "cover",
                TestContext.Current.CancellationToken);
            await stateWriter.SetBackendIdentityAsync(
                id,
                ariaGid,
                TestContext.Current.CancellationToken);
            await stateWriter.UpdateProgressAsync(
                id,
                new DownloadProgress(42.5),
                TestContext.Current.CancellationToken);
            await stateWriter.UpdateOutputFileSizeAsync(
                id,
                "6 GB",
                TestContext.Current.CancellationToken);
            await stateWriter.PauseAsync(id, TestContext.Current.CancellationToken);
            await stateWriter.ConfirmPausedAsync(id, TestContext.Current.CancellationToken);
        }

        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        {
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var reopenedStorage = new DownloadTaskProjectionStore(
                tasks,
                historyService);
            var startup = await reopenedStorage.GetDownloadingStateAsync(
                TestContext.Current.CancellationToken);
            var restored = Assert.Single(startup.Projections);
            var restoredTask = Assert.Single(startup.Tasks);
            Assert.Equal("video.m4s", restoredTask.Plan.TransferFiles["video"]);
            Assert.Equal("audio.m4s", restoredTask.Plan.TransferFiles["audio"]);
            Assert.Equal("cover", Assert.Single(restoredTask.Transfer.CompletedFileKeys));
            Assert.Equal(DownloadStatus.Pause, restored.Downloading.DownloadStatus);
            Assert.Equal(42.5f, restored.Downloading.Progress);
            Assert.Equal("6 GB", restored.DownloadBase.FileSize);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT gid, download_files, downloaded_files, download_status, progress FROM downloading WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ariaGid, reader.GetString(0));
        using var downloadFiles = JsonDocument.Parse(reader.GetString(1));
        using var downloadedFiles = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal("video.m4s", downloadFiles.RootElement.GetProperty("video").GetString());
        Assert.Equal("audio.m4s", downloadFiles.RootElement.GetProperty("audio").GetString());
        Assert.Equal("cover", downloadedFiles.RootElement[0].GetString());
        Assert.Equal((int)DownloadStatus.Pause, reader.GetInt32(3));
        Assert.Equal(42.5f, reader.GetFloat(4));
    }

    [Fact]
    public async Task LegacyCompletionSequenceMovesTaskAtomicallyToHistory()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "completion.db");
        var downloadingItem = new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = "complete-task-01",
                Name = "Completed episode",
                FilePath = Path.Combine(_directory, "completed-episode")
            },
            Downloading = new Downloading
            {
                Id = "complete-task-01",
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        {
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var storage = new DownloadTaskProjectionStore(
                tasks,
                historyService);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await storage.AddDownloadingAsync(tasks, downloadingItem, TestContext.Current.CancellationToken);
            var id = new DownloadTaskId(downloadingItem.DownloadBase.Id);
            await stateWriter.StartAsync(id, TestContext.Current.CancellationToken);
            await stateWriter.UpdateProgressAsync(
                id,
                new DownloadProgress(100),
                TestContext.Current.CancellationToken);
            await stateWriter.CompleteAsync(
                id,
                new DownloadCompletion(1234, "finished", "24 Mbps"),
                TestContext.Current.CancellationToken);
        }

        using var reopenedStore = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(database),
            new SystemClock());
        var reopenedClock = new SystemClock();
        var reopenedHistoryService = DownloadHistoryService.CreateForSharedStore(reopenedStore);
        using var reopenedTasks = new DownloadTaskApplicationService(
            reopenedStore,
            reopenedHistoryService,
            reopenedClock);
        using var reopened = new DownloadTaskProjectionStore(
            reopenedTasks,
            reopenedHistoryService);
        var startup = await reopened.GetDownloadingStateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(startup.Projections);
        var restored = Assert.Single(
            await reopened.GetDownloadedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("complete-task-01", restored.DownloadBase.Id);
        Assert.Equal(1234, restored.Downloaded.FinishedTimestamp);
    }

    [Fact]
    public async Task PersistedDomainChangesNotifyEveryAffectedUiBinding()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "projection-notifications.db");
        var item = new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = "projection-notifications",
                Name = "Projection",
                FilePath = Path.Combine(_directory, "projection")
            },
            Downloading = new Downloading
            {
                Id = "projection-notifications",
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
        using var store = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(database),
            new SystemClock());
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var storage = new DownloadTaskProjectionStore(
            tasks,
            historyService);
        var stateWriter = new DownloadTaskStateWriter(tasks);
        await storage.AddDownloadingAsync(tasks, item, TestContext.Current.CancellationToken);
        var notifications = new List<string?>();
        item.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var id = new DownloadTaskId(item.DownloadBase.Id);

        await stateWriter.StartAsync(id, TestContext.Current.CancellationToken);
        await stateWriter.UpdateActivityAsync(
            id,
            "video",
            "downloading",
            TestContext.Current.CancellationToken);
        await stateWriter.UpdateProgressAsync(
            id,
            new DownloadProgress(50, 500, 1000, 3_000_000, "500 B/1 KB", "24 Mbps"),
            TestContext.Current.CancellationToken);
        await stateWriter.UpdateOutputFileSizeAsync(
            id,
            "1 KB",
            TestContext.Current.CancellationToken);

        Assert.Contains(nameof(DownloadingItem.DownloadContent), notifications);
        Assert.Contains(nameof(DownloadingItem.DownloadStatusTitle), notifications);
        Assert.Contains(nameof(DownloadingItem.Progress), notifications);
        Assert.Contains(nameof(DownloadingItem.DownloadingFileSize), notifications);
        Assert.Contains(nameof(DownloadingItem.SpeedDisplay), notifications);
        Assert.Contains(nameof(DownloadingItem.FileSize), notifications);
        Assert.Equal("video", item.DownloadContent);
        Assert.Equal("downloading", item.DownloadStatusTitle);
        Assert.Equal(50, item.Progress);
        Assert.Equal("24 Mbps", item.SpeedDisplay);
        Assert.Equal("1 KB", item.FileSize);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var databasePath in Directory.EnumerateFiles(
                         _directory,
                         "*.db",
                         SearchOption.TopDirectoryOnly))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = true,
                    DefaultTimeout = 5
                }.ToString());
                SqliteConnection.ClearPool(connection);
            }

            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class LiveProjectionFixture : IDisposable
    {
        private readonly SqliteDownloadTaskStore _store;
        private readonly DownloadTaskApplicationService _tasks;
        public DownloadTaskApplicationService Tasks => _tasks;
        public ControllableUiDispatcher Dispatcher { get; } = new();
        public DownloadTaskProjectionStore Projections { get; }
        public DownloadTaskStateWriter Writer { get; }
        public DownloadTaskId Id { get; } = new("coalesced-progress");
        public DownloadingItem Item { get; } = new()
        {
            DownloadBase = new DownloadBase { Id = "coalesced-progress", FilePath = "coalesced-progress" },
            Downloading = new Downloading { Id = "coalesced-progress", DownloadStatus = DownloadStatus.WaitForDownload }
        };

        public LiveProjectionFixture(string directory)
        {
            Directory.CreateDirectory(directory);
            Item.DownloadBase.FilePath = Path.Combine(directory, "coalesced-progress");
            var clock = new SystemClock();
            _store = new SqliteDownloadTaskStore(new SqliteDownloadTaskStoreOptions(
                Path.Combine(directory, "coalesced-progress.db")), clock);
            var history = DownloadHistoryService.CreateForSharedStore(_store);
            _tasks = new DownloadTaskApplicationService(_store, history, clock);
            Projections = new DownloadTaskProjectionStore(_tasks, history, Dispatcher);
            Writer = new DownloadTaskStateWriter(_tasks);
        }

        public async Task InitializeAsync()
        {
            await Projections.AddDownloadingAsync(_tasks, Item, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await Writer.StartAsync(Id, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await Projections.FlushPresentationAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            Dispatcher.Release();
            Projections.Dispose();
            _tasks.Dispose();
            _store.Dispose();
        }
    }

    private sealed class ControllableUiDispatcher : IUiDispatcher
    {
        private TaskCompletionSource? _release;
        public int Invocations { get; private set; }
        public void Block() => _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Interlocked.Exchange(ref _release, null)?.TrySetResult();

        public async Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Invocations++;
            if (_release is { } release) await release.Task.ConfigureAwait(false);
            action();
        }
    }
}
