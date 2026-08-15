using System.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadOutputSuffixScalingProbeTests
{
    [Fact]
    public async Task SameBaseNameSuffixAllocationReportsProbeScaling()
    {
        var reportPath =
            Environment.GetEnvironmentVariable("DOWNKYI_SUFFIX_SCALING_REPORT")
            ?? Path.Combine(
                Path.GetTempPath(),
                "downkyi-suffix-scaling-report.txt");

        var report = new List<string>
        {
            "DownKyi output suffix scaling probe",
            $"UTC: {DateTimeOffset.UtcNow:O}",
            "",
            "same-basename:",
            "N,probes,expected,elapsed_ms"
        };

        var scales = new[] { 64, 128, 256, 512 };

        foreach (var scale in scales)
        {
            var result = await RunSameBaseScaleAsync(
                    scale,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            const long expected = 0;

            report.Add(
                $"{scale}," +
                $"{result.ReservationProbes}," +
                $"{expected}," +
                $"{result.Elapsed.TotalMilliseconds:F3}");

            Assert.Equal(
                expected,
                result.ReservationProbes);
        }

        report.Add("");
        report.Add("distinct-basename:");
        report.Add("N,probes,expected,elapsed_ms");

        foreach (var scale in scales)
        {
            var result = await RunDistinctBaseScaleAsync(
                    scale,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            report.Add(
                $"{scale}," +
                $"{result.ReservationProbes}," +
                $"{scale}," +
                $"{result.Elapsed.TotalMilliseconds:F3}");

            Assert.Equal(0, result.ReservationProbes);
        }

        var directory = Path.GetDirectoryName(reportPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllLinesAsync(
                reportPath,
                report,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static async Task<ScaleResult> RunSameBaseScaleAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-suffix-scale",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var basePath =
                Path.Combine(root, "same-output");

            using var session =
                new AdmissionSession(databasePath);

            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < count; index++)
            {
                var item = CreateItem(
                    $"same-{count}-{index:D4}",
                    basePath);

                await session.AdmitAsync(
                        item,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            stopwatch.Stop();

            return new ScaleResult(
                session.ReservationProbeCount,
                stopwatch.Elapsed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static async Task<ScaleResult> RunDistinctBaseScaleAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-distinct-scale",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            using var session =
                new AdmissionSession(databasePath);

            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < count; index++)
            {
                var item = CreateItem(
                    $"distinct-{count}-{index:D4}",
                    Path.Combine(
                        root,
                        $"output-{index:D4}"));

                await session.AdmitAsync(
                        item,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            stopwatch.Stop();

            return new ScaleResult(
                session.ReservationProbeCount,
                stopwatch.Elapsed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static DownloadingItem CreateItem(
        string id,
        string basePath)
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = id,
                Bvid = $"BV-{id}",
                MainTitle = id,
                Name = id,
                FilePath = basePath
            },
            Downloading = new Downloading
            {
                Id = id,
                DownloadStatus =
                    DownloadStatus.WaitForDownload
            },
            PlayUrl = new PlayUrl()
        };
    }

    private sealed record ScaleResult(
        long ReservationProbes,
        TimeSpan Elapsed);

    private sealed class AdmissionSession : IDisposable
    {
        private readonly SqliteDownloadTaskStore _innerStore;

        private readonly CountingStore _store;

        private readonly DownloadTaskApplicationService _tasks;

        private readonly DownloadTaskProjectionStore _projections;

        private readonly DownloadTaskAdmissionService _admission;

        public AdmissionSession(string databasePath)
        {
            var clock = new SystemClock();

            _innerStore =
                new SqliteDownloadTaskStore(
                    new SqliteDownloadTaskStoreOptions(
                        databasePath),
                    clock);

            _store =
                new CountingStore(_innerStore);

            _tasks =
                new DownloadTaskApplicationService(
                    _store,
                    clock);

            _projections =
                new DownloadTaskProjectionStore(
                    _tasks,
                    clock);

            _admission =
                new DownloadTaskAdmissionService(
                    new DownloadListState(),
                    _tasks,
                    _projections,
                    new RecordingDownloadTaskQueue());
        }

        public long ReservationProbeCount =>
            _store.ReservationProbeCount;

        public Task AdmitAsync(
            DownloadingItem item,
            CancellationToken cancellationToken)
        {
            return _admission.AdmitAsync(
                item,
                autoAddNumberSuffix: true,
                cancellationToken);
        }

        public void Dispose()
        {
            _admission.Dispose();
            _projections.Dispose();
            _tasks.Dispose();
            _innerStore.Dispose();
        }
    }

    private sealed class CountingStore(
        IDownloadTaskStore inner)
        : IDownloadTaskStore
    {
        public long ReservationProbeCount { get; private set; }

        public Task InitializeAsync(
            CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken) =>
            inner.AddAsync(task, cancellationToken);

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(
                task,
                expectedVersion,
                cancellationToken);

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            inner.UpdateProgressAsync(
                progressWrite,
                cancellationToken);

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            inner.FindAsync(
                taskId,
                cancellationToken);

        public Task<IReadOnlyList<DownloadTask>>
            GetUnfinishedAsync(
                CancellationToken cancellationToken) =>
            inner.GetUnfinishedAsync(cancellationToken);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            ReservationProbeCount++;

            return inner.IsOutputPathReservedAsync(
                basePath,
                ignoreCase,
                cancellationToken);
        }

        public Task<DownloadHistoryPage>
            GetHistoryPageAsync(
                DownloadHistoryCursor? cursor,
                int pageSize,
                CancellationToken cancellationToken) =>
            inner.GetHistoryPageAsync(
                cursor,
                pageSize,
                cancellationToken);

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            inner.DeleteAsync(
                taskId,
                cancellationToken);

        public Task<OperationResult> ClearHistoryAsync(
            CancellationToken cancellationToken) =>
            inner.ClearHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>>
            GetQuarantinedRecordsAsync(
                CancellationToken cancellationToken) =>
            inner.GetQuarantinedRecordsAsync(
                cancellationToken);
    }
}
