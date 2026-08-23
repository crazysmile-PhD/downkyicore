using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadTaskProjectionStoreResumeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-storage-resume-tests",
        Guid.NewGuid().ToString("N"));

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
            using var tasks = new DownloadTaskApplicationService(store, clock);
            using var storage = new DownloadTaskProjectionStore(tasks, clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
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
            using var tasks = new DownloadTaskApplicationService(store, clock);
            using var reopenedStorage = new DownloadTaskProjectionStore(tasks, clock);
            var restored = Assert.Single(
                await reopenedStorage.GetDownloadingAsync(TestContext.Current.CancellationToken));
            Assert.Equal(ariaGid, restored.Downloading.Gid);
            Assert.Equal("video.m4s", restored.Downloading.DownloadFiles["video"]);
            Assert.Equal("audio.m4s", restored.Downloading.DownloadFiles["audio"]);
            Assert.Equal("cover", Assert.Single(restored.Downloading.DownloadedFiles));
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
            using var tasks = new DownloadTaskApplicationService(store, clock);
            using var storage = new DownloadTaskProjectionStore(tasks, clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await storage.AddDownloadingAsync(downloadingItem, TestContext.Current.CancellationToken);
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
        using var reopenedTasks = new DownloadTaskApplicationService(reopenedStore, reopenedClock);
        using var reopened = new DownloadTaskProjectionStore(reopenedTasks, reopenedClock);
        Assert.Empty(await reopened.GetDownloadingAsync(TestContext.Current.CancellationToken));
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
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        var stateWriter = new DownloadTaskStateWriter(tasks);
        await storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
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
}
