using System.Text.Json;
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
    public void AddDownloading_PreservesResumeIdentityFilesAndPausedStateAcrossReopen()
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
                DownloadFiles = new Dictionary<string, string>
                {
                    ["video"] = "video.m4s",
                    ["audio"] = "audio.m4s"
                },
                DownloadedFiles = new List<string> { "cover" },
                Progress = 42.5f
            }
        };
        item.DownloadBase.Id = taskId;
        item.DownloadBase.FilePath = Path.Combine(_directory, "episode-01");

        using (var storage = new DownloadStorageService(database))
        {
            storage.AddDownloading(item);
        }

        using (var reopenedStorage = new DownloadStorageService(database))
        {
        }

        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT gid, download_files, downloaded_files, download_status, progress FROM downloading WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(ariaGid, reader.GetString(0));
        using var downloadFiles = JsonDocument.Parse(reader.GetString(1));
        using var downloadedFiles = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal("video.m4s", downloadFiles.RootElement.GetProperty("video").GetString());
        Assert.Equal("audio.m4s", downloadFiles.RootElement.GetProperty("audio").GetString());
        Assert.Equal("cover", downloadedFiles.RootElement[0].GetString());
        Assert.Equal((int)DownloadStatus.Pause, reader.GetInt32(3));
        Assert.Equal(42.5f, reader.GetFloat(4));
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
