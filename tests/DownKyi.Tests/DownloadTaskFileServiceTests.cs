using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTaskFileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "downkyi-file-lifecycle-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackgroundDeletionUsesTaskStagingNotPersistedPublishedOrTransferPaths()
    {
        Directory.CreateDirectory(_directory);
        var taskId = new DownloadTaskId("delete-owned-staging");
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var stagedDirectory = staging.GetDirectory(taskId, Path.Combine(_directory, "output"));
        Directory.CreateDirectory(stagedDirectory);
        var staged = Path.Combine(stagedDirectory, "partial.m4s");
        var published = Path.Combine(_directory, "output.mp4");
        var foreignSidecar = published + ".aria2";
        await File.WriteAllBytesAsync(staged, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(published, [91, 0, 255, 17], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(foreignSidecar, [4], TestContext.Current.CancellationToken);
        var downloadBase = new DownloadBase { Id = taskId.Value, FilePath = Path.Combine(_directory, "output") };
        var item = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadFiles = new Dictionary<string, string> { ["media"] = published }
            }
        };
        var service = new DownloadTaskFileService(
            new AriaRuntimeClientRegistry(), NullLogger<DownloadTaskFileService>.Instance, staging);

        var result = await service.DeleteGeneratedFilesAsync(item, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(stagedDirectory));
        Assert.Equal(new byte[] { 91, 0, 255, 17 },
            await File.ReadAllBytesAsync(published, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(foreignSidecar));
        staging.CleanupCurrentSession();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
