using System.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class AdmissionBatchEndToEndProbeTests
{
    [Fact(Explicit = true)]
    public async Task CompareSingleAndBatchAdmissionEndToEnd()
    {
        const int taskCount = 2048;
        const int batchSize = 64;

        var cancellationToken =
            TestContext.Current.CancellationToken;

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "downkyi-admission-batch-e2e",
                Guid.NewGuid().ToString("N"));

        var reportPath =
            Environment.GetEnvironmentVariable(
                "DOWNKYI_ADMISSION_BATCH_E2E_REPORT")
            ?? Path.Combine(
                root,
                "admission-batch-e2e.txt");

        Directory.CreateDirectory(root);

        try
        {
            // JIT / SQLite warm-up outside timed scenarios.
            await RunScenarioAsync(
                    Path.Combine(root, "warmup"),
                    count: 8,
                    batchSize: null,
                    cancellationToken)
                .ConfigureAwait(true);

            var single =
                await RunScenarioAsync(
                        Path.Combine(root, "single"),
                        taskCount,
                        batchSize: null,
                        cancellationToken)
                    .ConfigureAwait(true);

            var batch =
                await RunScenarioAsync(
                        Path.Combine(root, "batch"),
                        taskCount,
                        batchSize,
                        cancellationToken)
                    .ConfigureAwait(true);

            var speedup =
                single.ElapsedMs /
                batch.ElapsedMs;

            var report =
                new[]
                {
                    "DownKyi admission end-to-end batch probe",
                    $"UTC: {DateTimeOffset.UtcNow:O}",
                    "",
                    $"Tasks: {taskCount}",
                    $"Batch size: {batchSize}",
                    "",
                    $"Single total ms: {single.ElapsedMs:F3}",
                    $"Single ms/task: {single.ElapsedMs / taskCount:F6}",
                    $"Single persisted: {single.Persisted}",
                    $"Single listed: {single.Listed}",
                    $"Single queued: {single.Queued}",
                    "",
                    $"Batch total ms: {batch.ElapsedMs:F3}",
                    $"Batch ms/task: {batch.ElapsedMs / taskCount:F6}",
                    $"Batch persisted: {batch.Persisted}",
                    $"Batch listed: {batch.Listed}",
                    $"Batch queued: {batch.Queued}",
                    "",
                    $"Speedup: {speedup:F3}x"
                };

            var directory =
                Path.GetDirectoryName(
                    reportPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllLinesAsync(
                    reportPath,
                    report,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection
                .ClearAllPools();

            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(
                        root,
                        recursive: true);
                }
                catch (IOException)
                {
                    // Benchmark result is more important than temp cleanup.
                }
            }
        }
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        string root,
        int count,
        int? batchSize,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);

        var database =
            Path.Combine(
                root,
                "downloads.db");

        using var store =
            new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(
                    database),
                new SystemClock());

        using var tasks =
            new DownloadTaskApplicationService(
                store,
                new SystemClock());

        using var projections =
            new DownloadTaskProjectionStore(
                tasks,
                new SystemClock());

        var lists =
            new DownloadListState();

        var queue =
            new RecordingDownloadTaskQueue();

        using var admission =
            new DownloadTaskAdmissionService(
                lists,
                tasks,
                projections,
                queue);

        var basePath =
            Path.Combine(
                root,
                "same-output");

        var items =
            Enumerable
                .Range(0, count)
                .Select(index =>
                    CreateItem(
                        $"task-{index:D6}",
                        basePath,
                        index + 1L))
                .ToArray();

        var stopwatch =
            Stopwatch.StartNew();

        if (batchSize is null)
        {
            foreach (var item in items)
            {
                await admission
                    .AdmitAsync(
                        item,
                        autoAddNumberSuffix: true,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        else
        {
            foreach (var chunk in
                     items.Chunk(batchSize.Value))
            {
                await admission
                    .AdmitManyAsync(
                        chunk,
                        autoAddNumberSuffix: true,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
        }

        stopwatch.Stop();

        var unfinished =
            await tasks
                .GetUnfinishedAsync(
                    cancellationToken)
                .ConfigureAwait(true);

        Assert.Equal(
            count,
            unfinished.Count);

        Assert.Equal(
            count,
            lists.Downloading.Count);

        Assert.Equal(
            count,
            queue.Enqueued.Count);

        // Verify the allocation sequence itself, not only counts.
        Assert.Equal(
            basePath,
            items[0].DownloadBase.FilePath);

        if (count > 1)
        {
            Assert.Equal(
                basePath + "(1)",
                items[1].DownloadBase.FilePath);

            Assert.Equal(
                basePath + $"({count - 1})",
                items[^1].DownloadBase.FilePath);
        }

        return new ScenarioResult(
            stopwatch.Elapsed.TotalMilliseconds,
            unfinished.Count,
            lists.Downloading.Count,
            queue.Enqueued.Count);
    }

    private static DownloadingItem CreateItem(
        string id,
        string filePath,
        long cid)
    {
        return new DownloadingItem
        {
            DownloadBase =
                new DownloadBase
                {
                    Id = id,
                    Avid = cid,
                    Bvid = $"BV-{id}",
                    Cid = cid,
                    FilePath = filePath,
                    Name = id,
                    Resolution =
                        new DownKyi.Core.BiliApi.BiliUtils.Quality
                        {
                            Id = 80,
                            Name = "1080P"
                        },
                    VideoCodecName = "AVC"
                },
            Downloading =
                new Downloading
                {
                    DownloadStatus =
                        DownloadStatus.NotStarted,
                    PlayStreamType =
                        DownKyi.Core.BiliApi.VideoStream
                            .PlayStreamType.Video
                },
            PlayUrl =
                new DownKyi.Core.BiliApi.VideoStream.Models
                    .PlayUrl()
        };
    }

    private sealed record ScenarioResult(
        double ElapsedMs,
        int Persisted,
        int Listed,
        int Queued);
}