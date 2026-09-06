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
        using var admission = CreateAdmission(listState, tasks, projections, queue);
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
        using var admission = CreateAdmission(
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
        using var admission = CreateAdmission(
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
        using var admission = CreateAdmission(
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
        using var admission = CreateAdmission(
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
    public async Task CollisionResolutionPreservesRawCandidateSpelling()
    {
        var basePath = Path.Combine(_directory, "cafe\u0301-output");

        var resolved = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
            basePath,
            autoAddNumberSuffix: true,
            static (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(basePath), resolved, ignoreCase: false);
    }

    [Fact]
    public async Task AdmissionFreezesPhysicalPathBeforeReservationPersistenceProjectionAndQueue()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queue = new AdmissionObservingQueue(listState, projections);
        var logicalBasePath = Path.Combine(_directory, "logical-alias", "cafe\u0301-output");
        var frozenBasePath = Path.Combine(_directory, "physical-target", "cafe\u0301-output");
        var resolver = new RecordingPhysicalOutputPathResolver(_ => frozenBasePath);
        using var admission = CreateAdmission(listState, tasks, projections, queue, resolver);
        var item = CreateItem("frozen", logicalBasePath);

        await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var persisted = Assert.Single(await tasks.GetUnfinishedAsync(
            TestContext.Current.CancellationToken));
        var taskId = new DownloadTaskId(item.DownloadBase.Id);
        Assert.Equal([logicalBasePath], resolver.Inputs);
        Assert.Equal([frozenBasePath], store.ReservationPaths);
        Assert.Equal(frozenBasePath, item.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(frozenBasePath, persisted.Output.BasePath, ignoreCase: false);
        Assert.Equal(
            frozenBasePath,
            projections.GetRequiredSnapshot(taskId).Output.BasePath,
            ignoreCase: false);
        Assert.Same(item, Assert.Single(listState.Downloading));
        Assert.Equal(taskId, Assert.Single(queue.Enqueued));
        Assert.Equal([frozenBasePath], queue.ObservedPaths);
        Assert.DoesNotContain(
            logicalBasePath,
            new[] { item.DownloadBase.FilePath, persisted.Output.BasePath },
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolverFailureHasNoPersistenceListOrQueueSideEffects()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        var logicalBasePath = Path.Combine(_directory, "broken-alias", "output");
        var resolver = new RecordingPhysicalOutputPathResolver(
            _ => throw new IOException("Alias resolution failed."));
        using var admission = CreateAdmission(listState, tasks, projections, queue, resolver);
        var item = CreateItem("unresolved", logicalBasePath);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal([logicalBasePath], resolver.Inputs);
        Assert.Equal(logicalBasePath, item.DownloadBase.FilePath, ignoreCase: false);
        Assert.Empty(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Empty(listState.Downloading);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotPublishToListOrQueue()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore) { RejectAdds = true };
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(listState, tasks, projections, queue);
        var item = CreateItem("rejected", Path.Combine(_directory, "rejected-output"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        Assert.Empty(await innerStore.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Empty(listState.Downloading);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task AliasesSharePhysicalCollisionSequenceAndRemainFrozenAfterRetarget()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var frozenBasePath = Path.Combine(_directory, "physical-target", "output");
        var currentTarget = frozenBasePath;
        var resolver = new RecordingPhysicalOutputPathResolver(_ => currentTarget);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue(),
            resolver);
        var first = CreateItem("first-alias", Path.Combine(_directory, "alias-a", "output"));
        var second = CreateItem("second-alias", Path.Combine(_directory, "alias-b", "output"));

        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        currentTarget = Path.Combine(_directory, "retargeted", "output");
        Assert.Equal(frozenBasePath, first.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(
            frozenBasePath,
            projections.GetRequiredSnapshot(new DownloadTaskId(first.DownloadBase.Id)).Output.BasePath,
            ignoreCase: false);
        currentTarget = frozenBasePath;
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(frozenBasePath, first.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal($"{frozenBasePath}(1)", second.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(2, resolver.Inputs.Count);
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
        using var admission = CreateAdmission(
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

    private static DownloadTaskAdmissionService CreateAdmission(
        DownloadListState listState,
        IDownloadTaskApplicationService tasks,
        DownloadTaskProjectionStore projections,
        IDownloadTaskQueue queue,
        IPhysicalOutputPathResolver? resolver = null)
    {
        return new DownloadTaskAdmissionService(
            listState,
            tasks,
            projections,
            queue,
            resolver ?? new RecordingPhysicalOutputPathResolver(static path => path));
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
        public bool RejectAdds { get; init; }

        public int GetUnfinishedCallCount { get; private set; }

        public int ReservationProbeCount { get; private set; }

        public List<string> ReservationPaths { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken) => RejectAdds
                ? Task.FromResult(OperationResult.Failure(new OperationError(
                    "test.persistence_rejected",
                    "Persistence rejected the task.")))
                : inner.AddAsync(task, cancellationToken);

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
            ReservationPaths.Add(basePath);
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

    private sealed class RecordingPhysicalOutputPathResolver(Func<string, string> resolve)
        : IPhysicalOutputPathResolver
    {
        public List<string> Inputs { get; } = [];

        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            Inputs.Add(logicalBasePath);
            return resolve(logicalBasePath);
        }
    }

    private sealed class AdmissionObservingQueue(
        DownloadListState listState,
        DownloadTaskProjectionStore projections) : IDownloadTaskQueue
    {
        public List<DownloadTaskId> Enqueued { get; } = [];

        public List<string> ObservedPaths { get; } = [];

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listItem = Assert.Single(
                listState.Downloading,
                item => item.DownloadBase.Id == taskId.Value);
            Assert.Equal(
                listItem.DownloadBase.FilePath,
                projections.GetRequiredSnapshot(taskId).Output.BasePath,
                ignoreCase: false);
            Enqueued.Add(taskId);
            ObservedPaths.Add(listItem.DownloadBase.FilePath);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId) => Task.FromResult(true);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
