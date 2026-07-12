using System.Text.Json;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadStorageResumeTests : IDisposable
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
                Gid = ariaGid,
                DownloadStatus = DownloadStatus.Pause,
                Progress = 42.5f
            }
        };
        item.Downloading.DownloadFiles["video"] = "video.m4s";
        item.Downloading.DownloadFiles["audio"] = "audio.m4s";
        item.Downloading.DownloadedFiles.Add("cover");
        item.DownloadBase.Id = taskId;
        item.DownloadBase.FilePath = Path.Combine(_directory, "episode-01");

        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        using (var storage = new DownloadStorageService(store, new SystemClock()))
        {
            await storage.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
        }

        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        using (var reopenedStorage = new DownloadStorageService(store, new SystemClock()))
        {
            var restored = Assert.Single(
                await reopenedStorage.GetDownloadingAsync(TestContext.Current.CancellationToken));
            Assert.Equal(ariaGid, restored.Downloading.Gid);
            Assert.Equal("video.m4s", restored.Downloading.DownloadFiles["video"]);
            Assert.Equal("audio.m4s", restored.Downloading.DownloadFiles["audio"]);
            Assert.Equal("cover", Assert.Single(restored.Downloading.DownloadedFiles));
            Assert.Equal(DownloadStatus.Pause, restored.Downloading.DownloadStatus);
            Assert.Equal(42.5f, restored.Downloading.Progress);
        }

        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
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
                DownloadStatus = DownloadStatus.Downloading,
                Progress = 100
            }
        };
        var downloadedItem = new DownloadedItem
        {
            DownloadBase = downloadingItem.DownloadBase,
            Downloaded = new Downloaded
            {
                Id = "complete-task-01",
                FinishedTimestamp = 1234,
                FinishedTime = "finished",
                MaxSpeedDisplay = "24 Mbps"
            }
        };
        using (var store = new SqliteDownloadTaskStore(
                   new SqliteDownloadTaskStoreOptions(database),
                   new SystemClock()))
        using (var storage = new DownloadStorageService(store, new SystemClock()))
        {
            await storage.AddDownloadingAsync(downloadingItem, TestContext.Current.CancellationToken);
            await storage.RemoveDownloadingAsync(
                downloadingItem,
                cascadeRemove: false,
                cancellationToken: TestContext.Current.CancellationToken);
            await storage.AddDownloadedAsync(downloadedItem, TestContext.Current.CancellationToken);
        }

        using var reopenedStore = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(database),
            new SystemClock());
        using var reopened = new DownloadStorageService(reopenedStore, new SystemClock());
        Assert.Empty(await reopened.GetDownloadingAsync(TestContext.Current.CancellationToken));
        var restored = Assert.Single(
            await reopened.GetDownloadedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("complete-task-01", restored.DownloadBase.Id);
        Assert.Equal(1234, restored.Downloaded.FinishedTimestamp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
