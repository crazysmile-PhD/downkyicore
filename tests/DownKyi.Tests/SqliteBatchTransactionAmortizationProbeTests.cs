using System.Diagnostics;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class SqliteBatchTransactionAmortizationProbeTests
{
    [Fact(Explicit = true)]
    public async Task CompareSingleAndChunkedTransactions()
    {
        const int taskCount = 2048;
        const int batchSize = 64;

        var cancellationToken =
            TestContext.Current.CancellationToken;

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "downkyi-batch-transaction-probe",
                Guid.NewGuid().ToString("N"));

        var reportPath =
            Environment.GetEnvironmentVariable(
                "DOWNKYI_BATCH_TRANSACTION_REPORT")
            ?? Path.Combine(
                root,
                "batch-transaction-report.txt");

        Directory.CreateDirectory(root);

        try
        {
            await WarmUpAsync(
                root,
                cancellationToken);

            var singleTasks =
                CreateTasks(
                    root,
                    "single",
                    taskCount);

            var singleDatabase =
                Path.Combine(
                    root,
                    "single.db");

            double singleElapsedMs;

            using (var store =
                   new SqliteDownloadTaskStore(
                       new SqliteDownloadTaskStoreOptions(
                           singleDatabase),
                       new SystemClock()))
            {
                await store
                    .InitializeAsync(cancellationToken)
                    .ConfigureAwait(true);

                var stopwatch =
                    Stopwatch.StartNew();

                foreach (var task in singleTasks)
                {
                    var result =
                        await store
                            .AddAsync(
                                task,
                                cancellationToken)
                            .ConfigureAwait(true);

                    Assert.True(
                        result.IsSuccess,
                        result.Error?.Message);
                }

                stopwatch.Stop();

                singleElapsedMs =
                    stopwatch.Elapsed.TotalMilliseconds;

                var persisted =
                    await store
                        .GetUnfinishedAsync(
                            cancellationToken)
                        .ConfigureAwait(true);

                Assert.Equal(
                    taskCount,
                    persisted.Count);
            }

            var batchTasks =
                CreateTasks(
                    root,
                    "batch",
                    taskCount);

            var batchDatabase =
                Path.Combine(
                    root,
                    "batch.db");

            double batchElapsedMs;

            using (var store =
                   new SqliteDownloadTaskStore(
                       new SqliteDownloadTaskStoreOptions(
                           batchDatabase),
                       new SystemClock()))
            {
                await store
                    .InitializeAsync(cancellationToken)
                    .ConfigureAwait(true);

                var stopwatch =
                    Stopwatch.StartNew();

                foreach (var chunk in
                         batchTasks.Chunk(batchSize))
                {
                    var result =
                        await store
                            .AddManyAtomicAsync(
                                chunk,
                                cancellationToken)
                            .ConfigureAwait(true);

                    Assert.True(
                        result.IsSuccess,
                        result.Error?.Message);
                }

                stopwatch.Stop();

                batchElapsedMs =
                    stopwatch.Elapsed.TotalMilliseconds;

                var persisted =
                    await store
                        .GetUnfinishedAsync(
                            cancellationToken)
                        .ConfigureAwait(true);

                Assert.Equal(
                    taskCount,
                    persisted.Count);
            }

            var speedup =
                singleElapsedMs /
                batchElapsedMs;

            var report =
                new[]
                {
                    "DownKyi SQLite transaction amortization probe",
                    $"UTC: {DateTimeOffset.UtcNow:O}",
                    "",
                    $"Tasks: {taskCount}",
                    $"Batch size: {batchSize}",
                    $"Single transactions: {taskCount}",
                    $"Batch transactions: {taskCount / batchSize}",
                    "",
                    $"Single total ms: {singleElapsedMs:F3}",
                    $"Single ms/task: {singleElapsedMs / taskCount:F6}",
                    $"Batch total ms: {batchElapsedMs:F3}",
                    $"Batch ms/task: {batchElapsedMs / taskCount:F6}",
                    $"Speedup: {speedup:F3}x"
                };

            var reportDirectory =
                Path.GetDirectoryName(reportPath);

            if (!string.IsNullOrEmpty(reportDirectory))
            {
                Directory.CreateDirectory(
                    reportDirectory);
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
                    // Probe output matters more than temporary cleanup.
                }
            }
        }
    }

    private static async Task WarmUpAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var database =
            Path.Combine(
                root,
                "warmup.db");

        using var store =
            new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(
                    database),
                new SystemClock());

        await store
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(true);

        var tasks =
            CreateTasks(
                root,
                "warmup",
                8);

        foreach (var task in tasks)
        {
            var result =
                await store
                    .AddAsync(
                        task,
                        cancellationToken)
                    .ConfigureAwait(true);

            Assert.True(
                result.IsSuccess,
                result.Error?.Message);
        }
    }

    private static DownloadTask[] CreateTasks(
        string root,
        string prefix,
        int count)
    {
        return Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var id =
                    $"{prefix}-{index:D6}";

                var item =
                    new DownloadingItem
                    {
                        DownloadBase =
                            new DownloadBase
                            {
                                Id = id,
                                Avid = index + 1,
                                Bvid = $"BV-{id}",
                                Cid = index + 1,
                                FilePath =
                                    Path.Combine(
                                        root,
                                        $"{prefix}-output-{index:D6}"),
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

                return DownloadTaskProjectionMapper
                    .CreateNewTask(
                        item,
                        DateTimeOffset.UtcNow);
            })
            .ToArray();
    }
}