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

public sealed class DownloadSuffixAllocatorContractProbeTests
{
    [Fact]
    public async Task AllocatorContractMatrix()
    {
        var cancellationToken =
            TestContext.Current.CancellationToken;

        var reportPath =
            Environment.GetEnvironmentVariable(
                "DOWNKYI_ALLOCATOR_CONTRACT_REPORT")
            ?? Path.Combine(
                Path.GetTempPath(),
                "downkyi-allocator-contract.txt");

        var report = new List<string>
        {
            "DownKyi suffix allocator contract matrix",
            $"UTC: {DateTimeOffset.UtcNow:O}",
            ""
        };

        await ProbeHoleReuseAsync(
                report,
                cancellationToken)
            .ConfigureAwait(true);

        await ProbeRestartAsync(
                report,
                cancellationToken)
            .ConfigureAwait(true);

        await ProbeDiskOccupiesDatabaseHoleAsync(
                report,
                cancellationToken)
            .ConfigureAwait(true);

        await ProbeIndependentServiceRaceAsync(
                report,
                cancellationToken)
            .ConfigureAwait(true);

        var directory =
            Path.GetDirectoryName(reportPath);

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

    private static async Task ProbeHoleReuseAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("hole");
        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var basePath =
                Path.Combine(root, "same-output");

            using var session =
                new AdmissionSession(databasePath);

            var first =
                CreateItem("hole-0", basePath);

            var second =
                CreateItem("hole-1", basePath);

            var third =
                CreateItem("hole-2", basePath);

            await session.AdmitAsync(
                    first,
                    cancellationToken)
                .ConfigureAwait(true);

            await session.AdmitAsync(
                    second,
                    cancellationToken)
                .ConfigureAwait(true);

            await session.AdmitAsync(
                    third,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(
                Path.GetFullPath(basePath),
                first.DownloadBase.FilePath);

            Assert.Equal(
                $"{Path.GetFullPath(basePath)}(1)",
                second.DownloadBase.FilePath);

            Assert.Equal(
                $"{Path.GetFullPath(basePath)}(2)",
                third.DownloadBase.FilePath);

            var delete =
                await session.Tasks.DeleteAsync(
                        new DownloadTaskId("hole-1"),
                        cancellationToken)
                    .ConfigureAwait(true);

            Assert.True(delete.IsSuccess);

            var replacement =
                CreateItem("hole-replacement", basePath);

            await session.AdmitAsync(
                    replacement,
                    cancellationToken)
                .ConfigureAwait(true);

            report.Add(
                "HOLE_REUSE=" +
                replacement.DownloadBase.FilePath);

            report.Add(
                replacement.DownloadBase.FilePath.EndsWith(
                    "(1)",
                    StringComparison.Ordinal)
                    ? "HOLE_POLICY=FIRST_FREE"
                    : "HOLE_POLICY=MONOTONIC_OR_OTHER");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ProbeRestartAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("restart");
        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var basePath =
                Path.Combine(root, "same-output");

            using (var firstSession =
                   new AdmissionSession(databasePath))
            {
                await firstSession.AdmitAsync(
                        CreateItem("restart-0", basePath),
                        cancellationToken)
                    .ConfigureAwait(true);

                await firstSession.AdmitAsync(
                        CreateItem("restart-1", basePath),
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            using var reopened =
                new AdmissionSession(databasePath);

            var afterRestart =
                CreateItem(
                    "restart-after",
                    basePath);

            await reopened.AdmitAsync(
                    afterRestart,
                    cancellationToken)
                .ConfigureAwait(true);

            report.Add(
                "RESTART_NEXT=" +
                afterRestart.DownloadBase.FilePath);

            Assert.Equal(
                $"{Path.GetFullPath(basePath)}(2)",
                afterRestart.DownloadBase.FilePath);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ProbeDiskOccupiesDatabaseHoleAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("disk-hole");
        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var basePath =
                Path.Combine(root, "same-output");

            using var session =
                new AdmissionSession(databasePath);

            await session.AdmitAsync(
                    CreateItem("disk-0", basePath),
                    cancellationToken)
                .ConfigureAwait(true);

            await session.AdmitAsync(
                    CreateItem("disk-1", basePath),
                    cancellationToken)
                .ConfigureAwait(true);

            var delete =
                await session.Tasks.DeleteAsync(
                        new DownloadTaskId("disk-1"),
                        cancellationToken)
                    .ConfigureAwait(true);

            Assert.True(delete.IsSuccess);

            var physicalOccupant =
                $"{Path.GetFullPath(basePath)}(1).mp4";

            await File.WriteAllTextAsync(
                    physicalOccupant,
                    "foreign output",
                    cancellationToken)
                .ConfigureAwait(true);

            var replacement =
                CreateItem(
                    "disk-replacement",
                    basePath);

            await session.AdmitAsync(
                    replacement,
                    cancellationToken)
                .ConfigureAwait(true);

            report.Add(
                "DISK_HOLE_NEXT=" +
                replacement.DownloadBase.FilePath);

            Assert.Equal(
                $"{Path.GetFullPath(basePath)}(2)",
                replacement.DownloadBase.FilePath);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ProbeIndependentServiceRaceAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("race");
        Directory.CreateDirectory(root);

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var basePath =
                Path.Combine(root, "same-output");

            // Initialize schema before the race so schema creation
            // cannot become the competing variable.
            using (var bootstrap =
                   new SqliteDownloadTaskStore(
                       new SqliteDownloadTaskStoreOptions(
                           databasePath),
                       new SystemClock()))
            {
                await bootstrap.InitializeAsync(
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            var rendezvous =
                new AsyncRendezvous(2);

            using var left =
                new AdmissionSession(
                    databasePath,
                    rendezvous);

            using var right =
                new AdmissionSession(
                    databasePath,
                    rendezvous);

            var leftItem =
                CreateItem("race-left", basePath);

            var rightItem =
                CreateItem("race-right", basePath);

            var leftTask =
                Record.ExceptionAsync(
                    () => left.AdmitAsync(
                        leftItem,
                        cancellationToken))
                    .AsTask();

            var rightTask =
                Record.ExceptionAsync(
                    () => right.AdmitAsync(
                        rightItem,
                        cancellationToken))
                    .AsTask();

            var exceptions =
                await Task.WhenAll(
                        leftTask,
                        rightTask)
                    .ConfigureAwait(true);

            var successCount =
                exceptions.Count(
                    exception => exception is null);

            var failureCount =
                exceptions.Length - successCount;

            using var inspection =
                new AdmissionSession(databasePath);

            var persisted =
                await inspection.Tasks
                    .GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            var paths =
                persisted
                    .Select(task => task.Output.BasePath)
                    .OrderBy(
                        value => value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            report.Add(
                $"CROSS_INSTANCE_SUCCESS={successCount}");

            report.Add(
                $"CROSS_INSTANCE_FAILURE={failureCount}");

            report.Add(
                $"CROSS_INSTANCE_PERSISTED={persisted.Count}");

            report.Add(
                "CROSS_INSTANCE_PATHS=" +
                string.Join(" | ", paths));

            for (var index = 0;
                 index < exceptions.Length;
                 index++)
            {
                var exception = exceptions[index];

                report.Add(
                    $"CROSS_INSTANCE_EXCEPTION_{index}=" +
                    (exception is null
                        ? "NONE"
                        : $"{exception.GetType().Name}: " +
                          exception.Message));
            }

            // Safety invariant:
            // Regardless of availability behavior, the durable store
            // must never contain duplicate reservation paths.
            Assert.Equal(
                paths.Length,
                paths.Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Count());

            Assert.InRange(
                persisted.Count,
                1,
                2);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateRoot(string name)
    {
        return Path.Combine(
            Path.GetTempPath(),
            "downkyi-allocator-contract",
            name,
            Guid.NewGuid().ToString("N"));
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(root))
        {
            Directory.Delete(
                root,
                recursive: true);
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

    private sealed class AdmissionSession : IDisposable
    {
        private readonly SqliteDownloadTaskStore _innerStore;

        private readonly DownloadTaskProjectionStore _projections;

        private readonly DownloadTaskAdmissionService _admission;

        public AdmissionSession(
            string databasePath,
            AsyncRendezvous? rendezvous = null)
        {
            var clock = new SystemClock();

            _innerStore =
                new SqliteDownloadTaskStore(
                    new SqliteDownloadTaskStoreOptions(
                        databasePath),
                    clock);

            IDownloadTaskStore store =
                rendezvous is null
                    ? _innerStore
                    : new RendezvousStore(
                        _innerStore,
                        rendezvous);

            Tasks =
                new DownloadTaskApplicationService(
                    store,
                    clock);

            _projections =
                new DownloadTaskProjectionStore(
                    Tasks,
                    clock);

            _admission =
                new DownloadTaskAdmissionService(
                    new DownloadListState(),
                    Tasks,
                    _projections,
                    new RecordingDownloadTaskQueue());
        }

        public DownloadTaskApplicationService Tasks { get; }

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
            Tasks.Dispose();
            _innerStore.Dispose();
        }
    }

    private sealed class AsyncRendezvous(int participants)
    {
        private readonly TaskCompletionSource _released =
            new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(
                    ref _arrived) == participants)
            {
                _released.TrySetResult();
            }

            return _released.Task;
        }
    }

    private sealed class RendezvousStore(
        IDownloadTaskStore inner,
        AsyncRendezvous rendezvous)
        : IDownloadTaskStore
    {
        private int _reservationProbeCount;

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

        public async Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            var result =
                await inner.IsOutputPathReservedAsync(
                        basePath,
                        ignoreCase,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (Interlocked.Increment(
                    ref _reservationProbeCount) == 1)
            {
                await rendezvous.ArriveAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
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
