using System.Diagnostics;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Download;
using Microsoft.Data.Sqlite;

namespace DownKyi.SystemBenchmarks;

internal static class DownloadRestoreScenario
{
    public static async Task<SystemBenchmarkResult> RunAsync(
        string dataRoot,
        int taskCount,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "restore.db");
        var store = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(databasePath),
            new SystemClock());
        var projection = new DownloadTaskProjectionStore(store, new SystemClock());
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var createdAtUtc = DateTimeOffset.UnixEpoch;
            for (var index = 0; index < taskCount; index++)
            {
                var result = await store
                    .AddAsync(
                        DownloadBenchmarkData.CreateDownloadingTask(index, createdAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error?.Message ?? "Unable to seed restore dataset.");
                }
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var baselineWorkingSet = process.WorkingSet64;
            var peakWorkingSet = baselineWorkingSet;
            var stopwatch = Stopwatch.StartNew();
            var restoreTask = projection.GetDownloadingAsync(cancellationToken);
            while (!restoreTask.IsCompleted)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }

            var items = await restoreTask.ConfigureAwait(false);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            stopwatch.Stop();
            if (items.Count != taskCount)
            {
                throw new InvalidOperationException("Restore result count does not match the isolated dataset.");
            }

            return new SystemBenchmarkResult(
                "unfinished_task_restore",
                $"unfinished_tasks={taskCount}; sqlite_bytes={GetDatabaseBytes(databasePath)}",
                "sqlite-projection",
                Available: true,
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["elapsed_milliseconds"] = stopwatch.Elapsed.TotalMilliseconds,
                    ["baseline_working_set_bytes"] = baselineWorkingSet,
                    ["peak_working_set_bytes"] = peakWorkingSet,
                    ["peak_working_set_delta_bytes"] = Math.Max(0, peakWorkingSet - baselineWorkingSet),
                    ["restored_tasks"] = items.Count
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["elapsed_milliseconds"] = "ms",
                    ["baseline_working_set_bytes"] = "bytes",
                    ["peak_working_set_bytes"] = "bytes",
                    ["peak_working_set_delta_bytes"] = "bytes",
                    ["restored_tasks"] = "count"
                },
                "Dataset seeding and forced GC are outside the measured interval; the interval includes SQLite materialization and legacy UI projection.");
        }
        finally
        {
            projection.Dispose();
            store.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private static long GetDatabaseBytes(string databasePath)
    {
        return new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" }
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
    }
}
