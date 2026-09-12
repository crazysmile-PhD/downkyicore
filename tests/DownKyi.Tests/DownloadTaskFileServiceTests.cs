using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTaskFileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "downkyi-file-lifecycle-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPublishReopensAndReconcilesOnlyMatchingDestination(
        bool replaceDestination)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "download.db");
        var outputBase = Path.Combine(_directory, "output");
        var destination = outputBase + ".mp4";
        var taskId = new DownloadTaskId("failed-publish-commit");
        var downloadBase = new DownloadBase { Id = taskId.Value, FilePath = outputBase };
        var item = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
        var originalBytes = new byte[] { 7, 8, 9 };
        string stagedFile;
        using (var store = CreateStore(databasePath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            using var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
            await projections.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
            var writer = new DownloadTaskStateWriter(tasks);
            await writer.StartAsync(taskId, TestContext.Current.CancellationToken);
            var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
            using var settings = new DownKyi.Core.Settings.SettingsStore(
                Path.Combine(_directory, "settings.json"));
            var context = DownloadExecutionContextTestFactory.Create(item, settings.Current);
            context.StagingDirectory = staging.GetDirectory(taskId, outputBase,
                projections.GetRequiredSnapshot(taskId).Output.StagingToken);
            stagedFile = context.WorkingBasePath + ".mp4";
            await File.WriteAllBytesAsync(stagedFile, originalBytes,
                TestContext.Current.CancellationToken);
            using (var connection = CreateConnection(databasePath))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fail_publication_commit
                    BEFORE UPDATE OF publishing_key ON download_base
                    WHEN OLD.publishing_key IS NOT NULL AND NEW.publishing_key IS NULL
                    BEGIN SELECT RAISE(ABORT, 'injected final publication failure'); END;
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var service = new DownloadTaskFileService(new AriaRuntimeClientRegistry(),
                NullLogger<DownloadTaskFileService>.Instance, staging, writer);

            await Assert.ThrowsAnyAsync<Exception>(() => service.PublishAsync(
                context, "media", stagedFile, TestContext.Current.CancellationToken));
            await writer.FailAsync(taskId, new DownloadFailure(
                    "download.publish.failed", "Synthetic worker failure.", true),
                TestContext.Current.CancellationToken);
            Assert.False(File.Exists(stagedFile));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(destination,
                TestContext.Current.CancellationToken));
            var pending = await tasks.FindAsync(taskId, TestContext.Current.CancellationToken);
            Assert.NotNull(pending?.Output.PublishingArtifact);
            Assert.Equal(DownloadPhase.Failed, pending.Phase);
            Assert.Empty(pending.Output.PublishedArtifacts);
            staging.CleanupCurrentSession();
        }

        using (var connection = CreateConnection(databasePath))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_publication_commit;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var foreignBytes = new byte[] { 91, 0, 255, 17 };
        if (replaceDestination)
        {
            File.Delete(destination);
            await File.WriteAllBytesAsync(destination, foreignBytes,
                TestContext.Current.CancellationToken);
        }

        using var reopenedStore = CreateStore(databasePath);
        await reopenedStore.InitializeAsync(TestContext.Current.CancellationToken);
        using var reopenedTasks = new DownloadTaskApplicationService(reopenedStore, new SystemClock());
        var reopenedWriter = new DownloadTaskStateWriter(reopenedTasks);
        var reopenedStaging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var reopenedService = new DownloadTaskFileService(new AriaRuntimeClientRegistry(),
            NullLogger<DownloadTaskFileService>.Instance, reopenedStaging, reopenedWriter);
        var restored = Assert.IsType<DownloadTask>(
            await reopenedTasks.FindAsync(taskId, TestContext.Current.CancellationToken));
        Assert.NotNull(restored.Output.PublishingArtifact);

        var (reconciled, canRun) = await reopenedService.ReconcilePublishingAsync(
            restored, TestContext.Current.CancellationToken);

        Assert.Equal(!replaceDestination, canRun);
        Assert.Equal(DownloadPhase.Failed, reconciled.Phase);
        Assert.Equal(replaceDestination ? foreignBytes : originalBytes,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        if (replaceDestination)
        {
            Assert.NotNull(reconciled.Output.PublishingArtifact);
            Assert.Empty(reconciled.Output.PublishedArtifacts);
        }
        else
        {
            Assert.Null(reconciled.Output.PublishingArtifact);
            Assert.Equal(destination, reconciled.Output.PublishedArtifacts["media"]);
        }

        reopenedStaging.CleanupCurrentSession();
    }

    private static SqliteDownloadTaskStore CreateStore(string databasePath) =>
        new(new SqliteDownloadTaskStoreOptions(databasePath), new SystemClock());

    private static SqliteConnection CreateConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

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
            using var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "download.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(pooled);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
