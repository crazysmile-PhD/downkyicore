using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadTaskAdmissionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-admission-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentAdmissionsPersistDistinctOutputPathsBeforeQueueing()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = new DownloadTaskAdmissionService(listState, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "same-output");
        var first = CreateItem("first", basePath);
        var second = CreateItem("second", basePath);

        await Task.WhenAll(
            admission.AdmitAsync(first, true, TestContext.Current.CancellationToken),
            admission.AdmitAsync(second, true, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        var persisted = await tasks
            .GetUnfinishedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var persistedPaths = persisted.Select(task => task.Output.BasePath).ToArray();
        Assert.Equal(2, persistedPaths.Distinct(DownloadOutputPathResolver.PlatformComparer).Count());
        Assert.Contains(basePath, persistedPaths);
        Assert.Contains($"{basePath}(1)", persistedPaths);
        Assert.Equal(2, listState.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
    }

    [Fact]
    public async Task FailedRetryableTaskRetainsItsOutputReservation()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "retryable-output");
        var first = CreateItem("failed", basePath);
        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var taskId = new DownloadTaskId(first.DownloadBase.Id);
        Assert.True((await tasks
            .StartAsync(taskId, TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);
        Assert.True((await tasks
            .FailAsync(
                taskId,
                new DownloadFailure("download.failed", "Transfer failed.", true),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("second", basePath);
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", second.DownloadBase.FilePath);
    }

    [Fact]
    public async Task CanceledTaskRetainsItsOutputReservationUntilDeleted()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "canceled-output");
        var first = CreateItem("canceled", basePath);
        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True((await tasks
            .CancelAsync(new DownloadTaskId(first.DownloadBase.Id), TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("replacement", basePath);
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", second.DownloadBase.FilePath);

        Assert.True((await tasks
            .DeleteAsync(new DownloadTaskId(first.DownloadBase.Id), TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);
        var third = CreateItem("after-cleanup", basePath);
        await admission.AdmitAsync(third, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(Path.GetFullPath(basePath), third.DownloadBase.FilePath);
    }

    [Fact]
    public async Task AdmissionSkipsDiskCollisionCreatedAfterDraftConstruction()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "late-disk-collision");
        var draft = CreateItem("draft", basePath);
        await File.WriteAllTextAsync(
            $"{basePath}.mp4",
            "foreign output",
            TestContext.Current.CancellationToken);

        await admission.AdmitAsync(draft, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", draft.DownloadBase.FilePath);
    }

    [Fact]
    public async Task DisabledAutoSuffixRejectsCollisionWithoutRenamingOrPersisting()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "no-auto-suffix");
        await File.WriteAllTextAsync(
            $"{basePath}.mp4",
            "occupied",
            TestContext.Current.CancellationToken);
        var item = CreateItem("rejected", basePath);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            item,
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(basePath, item.DownloadBase.FilePath);
        Assert.Empty(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CaseInsensitivePathKeyTreatsMacStyleCaseVariantsAsOneOutput()
    {
        var first = Path.Combine(_directory, "Video");
        var second = Path.Combine(_directory, "video");

        Assert.Equal(
            DownloadOutputPathKey.Create(first, ignoreCase: true),
            DownloadOutputPathKey.Create(second, ignoreCase: true));
        Assert.NotEqual(
            DownloadOutputPathKey.Create(first, ignoreCase: false),
            DownloadOutputPathKey.Create(second, ignoreCase: false));
    }

    [Fact]
    public void PlatformComparisonIsCaseInsensitiveOnWindowsAndMacOS()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        Assert.Same(expected, DownloadOutputPathResolver.PlatformComparer);
    }

    [Fact]
    public async Task RepeatedAdmissionsProbeCandidatesWithoutReloadingAllUnfinishedTasks()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());

        for (var index = 0; index < 20; index++)
        {
            var item = CreateItem($"task-{index}", Path.Combine(_directory, $"output-{index}"));
            await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }

        Assert.Equal(0, store.GetUnfinishedCallCount);
        Assert.Equal(20, store.ReservationProbeCount);
    }

    private SqliteDownloadTaskStore CreateStore()
    {
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            new SystemClock());
    }

    private static DownloadingItem CreateItem(string id, string basePath)
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
                DownloadStatus = DownloadStatus.WaitForDownload
            },
            PlayUrl = new PlayUrl()
        };
    }

    private sealed class CountingDownloadTaskStore(IDownloadTaskStore inner) : IDownloadTaskStore
    {
        public int GetUnfinishedCallCount { get; private set; }

        public int ReservationProbeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken) => inner.AddAsync(task, cancellationToken);

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(task, expectedVersion, cancellationToken);

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            inner.UpdateProgressAsync(progressWrite, cancellationToken);

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => inner.FindAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken)
        {
            GetUnfinishedCallCount++;
            return inner.GetUnfinishedAsync(cancellationToken);
        }

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            ReservationProbeCount++;
            return inner.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken);
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            inner.GetHistoryPageAsync(cursor, pageSize, cancellationToken);

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => inner.DeleteAsync(taskId, cancellationToken);

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            inner.ClearHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => inner.GetQuarantinedRecordsAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
