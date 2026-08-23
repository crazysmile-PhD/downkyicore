using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadShutdownCoordinatorTests
{
    [Fact]
    public async Task StopAsyncCancellationWhileWorkerWaitsStillRecoversState()
    {
        using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var workerStopped = false;
        var workerTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, tokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
            }
            finally
            {
                workerStopped = true;
            }
        }, TestContext.Current.CancellationToken);
        var recoveryCount = 0;

        await DownloadShutdownCoordinator.StopAsync(
            tokenSource,
            [workerTask],
            TimeSpan.FromSeconds(1),
            _ => { },
            () =>
            {
                Assert.True(workerStopped);
                recoveryCount++;
                return Task.CompletedTask;
            });

        Assert.True(workerStopped);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task StopAsyncUnexpectedWorkerFailureRecoversBeforeRethrowing()
    {
        var recovered = false;
        var failure = new InvalidOperationException("worker failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadShutdownCoordinator.StopAsync(
                null,
                [Task.FromException(failure)],
                TimeSpan.FromSeconds(1),
                _ => { },
                () =>
                {
                    recovered = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(failure, exception);
        Assert.True(recovered);
    }

    [Fact]
    public async Task StopAsyncFailsClosedWhenOwnedWorkerMissesTimeout()
    {
        var workerCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutObserved = false;
        var recoveryCount = 0;

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                DownloadShutdownCoordinator.StopAsync(
                    null,
                    [workerCompletion.Task],
                    TimeSpan.Zero,
                    _ => timeoutObserved = true,
                    () =>
                    {
                        recoveryCount++;
                        return Task.CompletedTask;
                    }));

            Assert.True(timeoutObserved);
            Assert.Equal(1, recoveryCount);
            Assert.False(workerCompletion.Task.IsCompleted);
        }
        finally
        {
            workerCompletion.TrySetResult();
            await workerCompletion.Task.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ShutdownRecoveryQueuesActiveDomainTaskAndPreservesResumeData()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-shutdown-recovery-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "download.db");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var clock = new SystemClock();
            using var tasks = new DownloadTaskApplicationService(store, clock);
            using var projections = new DownloadTaskProjectionStore(tasks, clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var item = new DownloadingItem
            {
                DownloadBase = new DownloadBase
                {
                    Id = "shutdown-resume",
                    FilePath = Path.Combine(directory, "episode")
                },
                Downloading = new Downloading
                {
                    Id = "shutdown-resume",
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            await projections.AddDownloadingAsync(item, TestContext.Current.CancellationToken);
            var taskId = new DownloadTaskId(item.DownloadBase.Id);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
            await stateWriter.RecordTransferFileAsync(
                taskId,
                "video",
                "video.m4s",
                TestContext.Current.CancellationToken);
            await stateWriter.SetBackendIdentityAsync(
                taskId,
                "aria-gid",
                TestContext.Current.CancellationToken);
            await stateWriter.UpdateProgressAsync(
                taskId,
                new DownloadProgress(45, 450, 1000, 2_000_000),
                TestContext.Current.CancellationToken);
            var recovery = new DownloadTaskShutdownRecovery(tasks, stateWriter);

            await recovery.PersistAsync();

            var restored = Assert.IsType<DownloadTask>(
                await store.FindAsync(taskId, TestContext.Current.CancellationToken));
            Assert.Equal(DownloadPhase.Queued, restored.Phase);
            Assert.Equal("aria-gid", restored.Transfer.BackendIdentity);
            Assert.Equal("video.m4s", restored.Plan.TransferFiles["video"]);
            Assert.Equal(45, restored.Progress.Percentage);
            Assert.Equal(450, restored.Progress.DownloadedBytes);
        }
        finally
        {
            ClearOwnedSqlitePool(databasePath);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ClearOwnedSqlitePool(string databasePath)
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
}
