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
        var stagingToken = Guid.NewGuid().ToString("N");
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var stagedDirectory = staging.GetDirectory(
            taskId, Path.Combine(_directory, "output"), stagingToken);
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

        var task = CreateTask(taskId, downloadBase.FilePath, stagingToken);
        var result = await service.DeleteGeneratedFilesAsync(item, task,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(stagedDirectory));
        Assert.Equal(new byte[] { 91, 0, 255, 17 },
            await File.ReadAllBytesAsync(published, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(foreignSidecar));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public async Task FailedOwnedStagingCleanupDoesNotReportSuccessfulDeletion()
    {
        Directory.CreateDirectory(_directory);
        var taskId = new DownloadTaskId("cleanup-failure");
        var outputBase = Path.Combine(_directory, "output");
        var task = CreateTask(taskId, outputBase, Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.Combine(_directory, ".downkyi", "staging");
        var outside = Path.Combine(_directory, "user-files");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.mp4");
        await File.WriteAllBytesAsync(sentinel, [91, 0, 255, 17],
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(stagingRoot, task.Output.StagingToken), outside);
        var downloadBase = new DownloadBase { Id = taskId.Value, FilePath = outputBase };
        var item = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading { Id = taskId.Value, DownloadBase = downloadBase }
        };
        var service = new DownloadTaskFileService(new AriaRuntimeClientRegistry(),
            NullLogger<DownloadTaskFileService>.Instance,
            new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance));

        var result = await service.DeleteGeneratedFilesAsync(item, task,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(new byte[] { 91, 0, 255, 17 },
            await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken));
    }

    private static DownloadTask CreateTask(
        DownloadTaskId taskId, string outputBase, string stagingToken) =>
        DownloadTask.Create(taskId,
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1TEST", 1, 2, 3, 1, 1),
                "Collection", "Task", "00:01", "avc1",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty, string.Empty, 0),
            new DownloadPlan(DownloadContentSelection.None, [], 0, nfoRequest: null),
            new DownloadOutput(outputBase, null, stagingToken: stagingToken),
            DateTimeOffset.UnixEpoch);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
