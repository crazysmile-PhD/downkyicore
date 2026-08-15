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

public sealed class DownloadDiskClaimRaceProbeTests
{
    [Fact]
    public async Task PhysicalOutputAppearingAfterReservationProbeIsNotClaimedUnsuffixed()
    {
        var cancellationToken =
            TestContext.Current.CancellationToken;

        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-disk-claim-race",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        var reportPath =
            Environment.GetEnvironmentVariable(
                "DOWNKYI_DISK_RACE_REPORT")
            ?? Path.Combine(
                Path.GetTempPath(),
                "downkyi-disk-race-report.txt");

        try
        {
            var databasePath =
                Path.Combine(root, "download.db");

            var requestedPath =
                Path.Combine(root, "same-output");

            var expected =
                $"{Path.GetFullPath(requestedPath)}(1)";

            using var inner =
                new SqliteDownloadTaskStore(
                    new SqliteDownloadTaskStoreOptions(
                        databasePath),
                    new SystemClock());

            var injectingStore =
                new DiskInjectionStore(inner);

            var clock = new SystemClock();

            using var tasks =
                new DownloadTaskApplicationService(
                    injectingStore,
                    clock);

            using var projections =
                new DownloadTaskProjectionStore(
                    tasks,
                    clock);

            using var admission =
                new DownloadTaskAdmissionService(
                    new DownloadListState(),
                    tasks,
                    projections,
                    new RecordingDownloadTaskQueue());

            var item =
                CreateItem(
                    "disk-race",
                    requestedPath);

            await admission.AdmitAsync(
                    item,
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);

            var persisted =
                await tasks.GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            var physicalFile =
                $"{Path.GetFullPath(requestedPath)}.mp4";

            var report = new[]
            {
                "DownKyi filesystem claim race probe",
                $"UTC={DateTimeOffset.UtcNow:O}",
                $"REQUESTED={Path.GetFullPath(requestedPath)}",
                $"INJECTED_FILE={physicalFile}",
                $"INJECTED_FILE_EXISTS={File.Exists(physicalFile)}",
                $"ACTUAL={item.DownloadBase.FilePath}",
                $"EXPECTED={expected}",
                $"PERSISTED_COUNT={persisted.Count}",
                $"PERSISTED_PATH={persisted.Single().Output.BasePath}"
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

            Assert.True(
                File.Exists(physicalFile),
                "The deterministic race injector did not create the competing physical output.");

            Assert.Equal(
                expected,
                item.DownloadBase.FilePath);
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

    private sealed class DiskInjectionStore(
        IDownloadTaskStore inner)
        : IDownloadTaskStore
    {
        private int _injected;

        public Task InitializeAsync(
            CancellationToken cancellationToken) =>
            inner.InitializeAsync(
                cancellationToken);

        public async Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(
                    ref _injected,
                    1,
                    0) == 0)
            {
                await File.WriteAllTextAsync(
                        task.Output.BasePath + ".mp4",
                        "FOREIGN FILE - CREATED BETWEEN RESOLUTION AND CLAIM",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await inner
                .AddAsync(
                    task,
                    cancellationToken)
                .ConfigureAwait(false);
        }

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
            inner.GetUnfinishedAsync(
                cancellationToken);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            inner.IsOutputPathReservedAsync(
                basePath,
                ignoreCase,
                cancellationToken);

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
            inner.ClearHistoryAsync(
                cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>>
            GetQuarantinedRecordsAsync(
                CancellationToken cancellationToken) =>
            inner.GetQuarantinedRecordsAsync(
                cancellationToken);
    }
}
