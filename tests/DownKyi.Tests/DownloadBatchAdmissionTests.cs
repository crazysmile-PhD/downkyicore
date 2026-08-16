using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadBatchAdmissionTests
{
    [Fact]
    public async Task SameBaseNameBatchWithSuffixOffPreservesSequentialPartialSuccess()
    {
        var store = new ScriptedAtomicStore();
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        using var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
        var lists = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = new DownloadTaskAdmissionService(lists, tasks, projections, queue);
        var output = Path.Combine(Path.GetTempPath(), "batch-suffix-off", Guid.NewGuid().ToString("N"));
        var first = CreateItem("suffix-off-first", output, 1);
        var second = CreateItem("suffix-off-second", output, 2);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitManyAsync(
            [first, second], false, TestContext.Current.CancellationToken));

        Assert.Equal(output, first.DownloadBase.FilePath);
        Assert.Equal(output, second.DownloadBase.FilePath);
        Assert.Equal(["suffix-off-first"], store.AddedTaskIds);
        Assert.Equal(1, lists.Downloading.Count);
        Assert.Equal(1, queue.Enqueued.Count);
        Assert.Equal(0, store.AtomicCalls);
    }

    [Fact]
    public async Task KnownAtomicRollbackRestoresLogicalPathsBeforeSequentialReplay()
    {
        var store = new ScriptedAtomicStore
        {
            AtomicResult = OperationResult.Failure(new OperationError(
                "download.store.output_path_reserved", "Reserved.", OperationErrorKind.Conflict))
        };
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        using var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
        var lists = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = new DownloadTaskAdmissionService(lists, tasks, projections, queue);
        var output = Path.Combine(Path.GetTempPath(), "batch-fallback", Guid.NewGuid().ToString("N"));
        var first = CreateItem("fallback-first", output, 1);
        var second = CreateItem("fallback-second", output, 2);

        await admission.AdmitManyAsync([first, second], true, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.AtomicCalls);
        Assert.Empty(store.AtomicPersistedTaskIds);
        Assert.Equal([output, output + "(1)"], store.ReservationProbes);
        Assert.Equal(["fallback-first", "fallback-second"], store.AddedTaskIds);
        Assert.Equal(2, lists.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
    }

    [Fact]
    public async Task CancellationAfterAtomicCommitCompletesAllListAndQueueWork()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new ScriptedAtomicStore { AfterAtomicCommitAsync = cancellation.CancelAsync };
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        using var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
        var lists = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = new DownloadTaskAdmissionService(lists, tasks, projections, queue);
        var output = Path.Combine(Path.GetTempPath(), "batch-post-commit", Guid.NewGuid().ToString("N"));

        await admission.AdmitManyAsync(
            [CreateItem("post-commit-first", output, 1), CreateItem("post-commit-second", output, 2)],
            true,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, store.AtomicPersistedTaskIds.Count);
        Assert.Equal(2, lists.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
    }
    [Fact]
    public async Task SameBaseNameBatchGetsDistinctReservationsAndQueuesBoth()
    {
        var root = Path.Combine(Path.GetTempPath(), "downkyi-batch-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SqliteDownloadTaskStore(new SqliteDownloadTaskStoreOptions(Path.Combine(root, "downloads.db")), new SystemClock());
            using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            using var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
            var lists = new DownloadListState();
            var queue = new RecordingDownloadTaskQueue();
            using var admission = new DownloadTaskAdmissionService(lists, tasks, projections, queue);
            var output = Path.Combine(root, "same-output");
            var first = CreateItem("batch-1", output, 1);
            var second = CreateItem("batch-2", output, 2);

            await admission.AdmitManyAsync([first, second], true, TestContext.Current.CancellationToken);

            Assert.Equal(output, first.DownloadBase.FilePath);
            Assert.Equal(output + "(1)", second.DownloadBase.FilePath);
            Assert.Equal(2, (await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Count);
            Assert.Equal(2, lists.Downloading.Count);
            Assert.Equal(2, queue.Enqueued.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static DownloadingItem CreateItem(string id, string path, long cid) => new()
    {
        DownloadBase = new DownloadBase
        {
            Id = id, Avid = cid, Bvid = $"BV-{id}", Cid = cid, FilePath = path, Name = id,
            Resolution = new DownKyi.Core.BiliApi.BiliUtils.Quality { Id = 80, Name = "1080P" }, VideoCodecName = "AVC"
        },
        Downloading = new Downloading
        {
            DownloadStatus = DownloadStatus.NotStarted,
            PlayStreamType = DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video
        },
        PlayUrl = new DownKyi.Core.BiliApi.VideoStream.Models.PlayUrl()
    };

    private sealed class ScriptedAtomicStore : IDownloadTaskStore, IDownloadTaskAtomicBatchStore
    {
        private readonly Dictionary<string, DownloadTask> _active = [];

        public OperationResult AtomicResult { get; init; } = OperationResult.Success();
        public Func<Task>? AfterAtomicCommitAsync { get; init; }
        public int AtomicCalls { get; private set; }
        public List<string> AtomicPersistedTaskIds { get; } = [];
        public List<string> AddedTaskIds { get; } = [];
        public List<string> ReservationProbes { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(DownloadTask task, long expectedVersion, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> UpdateProgressAsync(DownloadProgressWrite progressWrite, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
        public Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => Task.FromResult(_active.GetValueOrDefault(taskId.Value));
        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadTask>>(_active.Values.ToArray());
        public Task<IReadOnlyList<string>> GetActiveOutputPathsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(_active.Values.Select(static task => task.Output.BasePath).ToArray());
        public Task<DownloadHistoryPage> GetHistoryPageAsync(DownloadHistoryCursor? cursor, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new DownloadHistoryPage([], null));
        public Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);

        public Task<bool> IsOutputPathReservedAsync(string basePath, bool ignoreCase, CancellationToken cancellationToken)
        {
            ReservationProbes.Add(basePath);
            return Task.FromResult(_active.Values.Any(task => string.Equals(task.Output.BasePath, basePath, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            AddedTaskIds.Add(task.Id.Value);
            _active.Add(task.Id.Value, task);
            return Task.FromResult(OperationResult.Success());
        }

        public async Task<OperationResult> AddManyAtomicAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken)
        {
            AtomicCalls++;
            if (!AtomicResult.IsSuccess) return AtomicResult;
            foreach (var task in tasks)
            {
                _active.Add(task.Id.Value, task);
                AtomicPersistedTaskIds.Add(task.Id.Value);
            }

            if (AfterAtomicCommitAsync != null) await AfterAtomicCommitAsync().ConfigureAwait(false);
            return OperationResult.Success();
        }
    }
}
