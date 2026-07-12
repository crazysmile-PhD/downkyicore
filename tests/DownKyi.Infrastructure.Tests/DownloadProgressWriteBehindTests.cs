using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Infrastructure.Tests;

public sealed class DownloadProgressWriteBehindTests
{
    [Fact]
    public async Task ContiguousProgressForOneTaskIsCoalescedIntoOneWrite()
    {
        var store = new RecordingStore();
        var clock = new GateClock();
        var writeBehind = new DownloadProgressWriteBehind(
            store,
            clock,
            TimeSpan.FromSeconds(1));
        await using var writeBehindScope = writeBehind.ConfigureAwait(true);
        var taskId = new DownloadTaskId("coalesced");

        Assert.True(writeBehind.TryQueue(CreateWrite(taskId, 0, 1, 10)));
        Assert.True(writeBehind.TryQueue(CreateWrite(taskId, 1, 2, 20)));
        Assert.True(writeBehind.TryQueue(CreateWrite(taskId, 2, 3, 30)));
        clock.Release();
        await store.WaitForWriteAsync(TestContext.Current.CancellationToken);

        var write = Assert.Single(store.Writes);
        Assert.Equal(0, write.ExpectedVersion);
        Assert.Equal(3, write.TargetVersion);
        Assert.Equal(30, write.Progress.Percentage);
    }

    [Fact]
    public async Task PendingTaskLimitRejectsNewTaskButAcceptsUpdatesForQueuedTask()
    {
        var store = new RecordingStore();
        var clock = new GateClock();
        var writeBehind = new DownloadProgressWriteBehind(
            store,
            clock,
            TimeSpan.FromSeconds(1),
            maximumPendingTasks: 1);
        await using var writeBehindScope = writeBehind.ConfigureAwait(true);
        var first = new DownloadTaskId("first");

        Assert.True(writeBehind.TryQueue(CreateWrite(first, 0, 1, 10)));
        Assert.True(writeBehind.TryQueue(CreateWrite(first, 1, 2, 20)));
        Assert.False(writeBehind.TryQueue(CreateWrite(new DownloadTaskId("second"), 0, 1, 10)));

        clock.Release();
        await store.WaitForWriteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(store.Writes).TargetVersion);
    }

    [Fact]
    public async Task DisposeInterruptsDelayAndFlushesPendingProgress()
    {
        var store = new RecordingStore();
        var clock = new GateClock();
        var writeBehind = new DownloadProgressWriteBehind(store, clock, TimeSpan.FromMinutes(1));
        Assert.True(writeBehind.TryQueue(CreateWrite(new DownloadTaskId("shutdown"), 0, 1, 10)));

        await writeBehind.DisposeAsync();

        Assert.Single(store.Writes);
        Assert.True(writeBehind.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StoreConflictIsCountedWithoutStoppingTheWorker()
    {
        var store = new RecordingStore { RejectWrites = true };
        var clock = new GateClock();
        var writeBehind = new DownloadProgressWriteBehind(
            store,
            clock,
            TimeSpan.FromSeconds(1));
        await using var writeBehindScope = writeBehind.ConfigureAwait(true);
        Assert.True(writeBehind.TryQueue(CreateWrite(new DownloadTaskId("conflict"), 0, 1, 10)));

        clock.Release();
        await store.WaitForWriteAsync(TestContext.Current.CancellationToken);
        await writeBehind.DisposeAsync();

        Assert.Equal(1, writeBehind.RejectedWriteCount);
        Assert.True(writeBehind.Completion.IsCompletedSuccessfully);
    }

    private static DownloadProgressWrite CreateWrite(
        DownloadTaskId taskId,
        long expectedVersion,
        long targetVersion,
        double percentage)
    {
        return new DownloadProgressWrite(
            taskId,
            new DownloadProgress(percentage),
            expectedVersion,
            targetVersion,
            DateTimeOffset.UnixEpoch.AddSeconds(targetVersion));
    }

    private sealed class GateClock : IClock
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class RecordingStore : IDownloadTaskStore
    {
        private readonly Lock _lock = new();
        private readonly List<DownloadProgressWrite> _writes = [];
        private readonly TaskCompletionSource _writeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RejectWrites { get; init; }

        public IReadOnlyList<DownloadProgressWrite> Writes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _writes];
                }
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _writes.Add(progressWrite);
            }

            _writeObserved.TrySetResult();
            var result = RejectWrites
                ? OperationResult.Failure(new OperationError(
                    "test.conflict",
                    "Rejected for test.",
                    OperationErrorKind.Conflict))
                : OperationResult.Success();
            return Task.FromResult(result);
        }

        public Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WaitForWriteAsync(CancellationToken cancellationToken)
        {
            return _writeObserved.Task.WaitAsync(cancellationToken);
        }
    }
}
