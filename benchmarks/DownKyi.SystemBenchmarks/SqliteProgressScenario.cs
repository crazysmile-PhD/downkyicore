using System.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Download;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.SystemBenchmarks;

internal static class SqliteProgressScenario
{
    public static async Task<SystemBenchmarkResult> RunAsync(
        string dataRoot,
        int taskCount,
        int attemptsPerTask,
        int samplesPerAttempt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "progress.db");
        var clock = new SystemClock();
        using var store = new SqliteDownloadTaskStore(new SqliteDownloadTaskStoreOptions(databasePath), clock);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var history = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, history, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, history);
        var writer = new DownloadTaskStateWriter(tasks);
        for (var index = 0; index < taskCount; index++)
        {
            var result = await tasks.AddAsync(
                DownloadBenchmarkData.CreateDownloadingTask(index, DateTimeOffset.UnixEpoch), cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "Unable to seed progress dataset.");
            }
        }

        await projections.GetDownloadingStateAsync(cancellationToken).ConfigureAwait(false);
        var committedUpdates = 0;
        tasks.TaskChanged += (_, args) =>
        {
            if (args.Snapshot?.Progress.Percentage == 100)
            {
                Interlocked.Increment(ref committedUpdates);
            }
        };
        var backend = new SamplingBackend(samplesPerAttempt);
        var coordinator = new DownloadTransferCoordinator(backend, new DownloadRetryPolicy(),
            TimeProvider.System, NullLogger<DownloadTransferCoordinator>.Instance);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, taskCount).Select(index => Task.Run(async () =>
        {
            var taskId = new DownloadTaskId($"benchmark-{index:D6}");
            for (var attempt = 0; attempt < attemptsPerTask; attempt++)
            {
                var request = DownloadTransferRequestFactory.Create(taskId,
                    ["https://synthetic.invalid/media"], dataRoot, dataRoot, $"media-{index}", 1000,
                    tasks, progress => projections.PublishLiveProgress(taskId, progress), writer,
                    static () => { }, cancellationToken);
                var result = await coordinator.TransferAsync(request,
                    static _ => throw new InvalidOperationException("The sampling backend cannot refresh."),
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome != DownloadTransferOutcome.Succeeded)
                {
                    throw new InvalidOperationException("The sampling transfer did not complete.");
                }
            }
        }, cancellationToken))).ConfigureAwait(false);
        stopwatch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var expectedCommits = checked(taskCount * attemptsPerTask);
        if (committedUpdates != expectedCommits)
        {
            throw new InvalidOperationException("The transfer lease did not commit exactly one progress update per attempt.");
        }

        var restored = await store.GetUnfinishedAsync(cancellationToken).ConfigureAwait(false);
        if (restored.Count != taskCount || restored.Any(task => task.Progress.Percentage != 100))
        {
            throw new InvalidOperationException("The committed transfer progress could not be restored.");
        }

        using var pooledConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(pooledConnection);
        return new SystemBenchmarkResult(
            "sqlite_transfer_progress_commits",
            $"tasks={taskCount}; attempts_per_task={attemptsPerTask}; samples_per_attempt={samplesPerAttempt}",
            "production-transfer-attempt-sqlite",
            Available: true,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["source_progress_samples"] = checked(expectedCommits * samplesPerAttempt),
                ["committed_progress_updates"] = committedUpdates,
                ["wall_elapsed_milliseconds"] = stopwatch.Elapsed.TotalMilliseconds,
                ["cpu_milliseconds"] = cpuMilliseconds,
                ["allocated_bytes"] = allocatedBytes
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_progress_samples"] = "count",
                ["committed_progress_updates"] = "count",
                ["wall_elapsed_milliseconds"] = "ms",
                ["cpu_milliseconds"] = "ms",
                ["allocated_bytes"] = "bytes"
            },
            "Uses the production request factory, transfer coordinator/attempt and Application/SQLite progress commit. " +
            "The sampling backend performs no network or file transfer; dataset seeding and restore validation are outside the measured interval. " +
            "This is a new series, not a continuation of the removed write-behind mechanism benchmark.");
    }

    private sealed class SamplingBackend(int samplesPerAttempt) : ITransferBackend
    {
        public string Name => "sampling";

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            for (var sample = 1; sample <= samplesPerAttempt; sample++)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                request.PublishProgress(new DownloadProgress(sample * 100d / samplesPerAttempt,
                    sample * 1000L / samplesPerAttempt, 1000));
            }

            return Task.FromResult(DownloadTransferResult.Succeeded());
        }
    }
}
