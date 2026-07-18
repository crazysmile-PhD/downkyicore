using System.Diagnostics;
using Downloader;

namespace DownKyi.SystemBenchmarks;

internal static class TransferThroughputScenario
{
    private static readonly int[] TaskCounts = [1, 4, 8];

    public static async Task<SystemBenchmarkResult> RunAsync(
        string dataRoot,
        long bytesPerTask,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        var server = new LoopbackRangeServer(bytesPerTask);
        server.Start();
        await using var serverScope = server.ConfigureAwait(false);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var taskCount in TaskCounts)
        {
            var scenarioDirectory = Path.Combine(dataRoot, $"tasks-{taskCount}");
            Directory.CreateDirectory(scenarioDirectory);
            var stopwatch = Stopwatch.StartNew();
            var downloads = Enumerable.Range(0, taskCount)
                .Select(index => DownloadAsync(
                    server.Address,
                    Path.Combine(scenarioDirectory, $"payload-{index:D2}.bin"),
                    cancellationToken))
                .ToArray();
            await Task.WhenAll(downloads).ConfigureAwait(false);
            stopwatch.Stop();

            var totalBytes = checked(bytesPerTask * taskCount);
            foreach (var path in Directory.GetFiles(scenarioDirectory, "payload-*.bin"))
            {
                if (new FileInfo(path).Length != bytesPerTask)
                {
                    throw new InvalidOperationException("Loopback transfer produced an incomplete file.");
                }
            }

            var bytesPerSecond = totalBytes / stopwatch.Elapsed.TotalSeconds;
            var prefix = $"tasks_{taskCount}";
            metrics[$"{prefix}_elapsed_milliseconds"] = stopwatch.Elapsed.TotalMilliseconds;
            metrics[$"{prefix}_megabytes_per_second"] = bytesPerSecond / 1_000_000d;
            metrics[$"{prefix}_megabits_per_second"] = bytesPerSecond * 8 / 1_000_000d;
            units[$"{prefix}_elapsed_milliseconds"] = "ms";
            units[$"{prefix}_megabytes_per_second"] = "MB/s";
            units[$"{prefix}_megabits_per_second"] = "Mbps";
        }

        return new SystemBenchmarkResult(
            "aggregate_transfer_throughput",
            $"bytes_per_task={bytesPerTask}; task_counts=1,4,8; split=8",
            "built-in-loopback-range-http",
            Available: true,
            metrics,
            units,
            "The source is an in-process loopback Range server, so this isolates the built-in downloader and local scheduler from Bilibili/CDN/network limits; results remain non-gating until runner variance is characterized.");
    }

    private static async Task DownloadAsync(
        Uri source,
        string destination,
        CancellationToken cancellationToken)
    {
        var configuration = new DownloadConfiguration
        {
            ChunkCount = 8,
            ParallelDownload = true,
            ParallelCount = 8,
            MaximumMemoryBufferBytes = 8 * 1024 * 1024,
            EnableAutoResumeDownload = false,
            ClearPackageOnCompletionWithFailure = true,
            FileExistPolicy = FileExistPolicy.IgnoreDownload
        };
        using var downloader = new Downloader.DownloadService(configuration);
        await downloader
            .DownloadFileTaskAsync(source.ToString(), destination, cancellationToken)
            .ConfigureAwait(false);
    }
}
