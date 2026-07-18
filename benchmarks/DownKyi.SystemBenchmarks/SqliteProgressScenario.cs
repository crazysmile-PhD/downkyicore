using System.Diagnostics;
using System.Threading.Channels;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.SystemBenchmarks;

internal static class SqliteProgressScenario
{
    public static async Task<SystemBenchmarkResult> RunAsync(
        string dataRoot,
        int taskCount,
        int simulatedSeconds,
        int samplesPerTaskSecond,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        var clock = new StepClock(DateTimeOffset.UnixEpoch);
        var innerStore = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(dataRoot, "progress.db")),
            clock);
        var store = new CountingStore(innerStore);
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
            {
                var result = await store
                    .AddAsync(
                        DownloadBenchmarkData.CreateDownloadingTask(taskIndex, clock.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error?.Message ?? "Unable to seed progress dataset.");
                }
            }

            var writeBehind = new DownloadProgressWriteBehind(
                store,
                clock,
                TimeSpan.FromSeconds(1),
                maximumPendingTasks: taskCount);
            await using var writeBehindScope = writeBehind.ConfigureAwait(false);
            var versions = Enumerable.Repeat(1L, taskCount).ToArray();
            var queuedSamples = 0L;
            var stopwatch = Stopwatch.StartNew();
            for (var second = 0; second < simulatedSeconds; second++)
            {
                for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
                {
                    var taskId = new DownloadTaskId($"benchmark-{taskIndex:D6}");
                    for (var sample = 0; sample < samplesPerTaskSecond; sample++)
                    {
                        var expectedVersion = versions[taskIndex];
                        var targetVersion = checked(expectedVersion + 1);
                        var progress = Math.Min(
                            99.9,
                            ((second * samplesPerTaskSecond) + sample + 1d)
                            / (simulatedSeconds * samplesPerTaskSecond) * 100);
                        if (!writeBehind.TryQueue(new DownloadProgressWrite(
                                taskId,
                                new DownloadProgress(progress),
                                expectedVersion,
                                targetVersion,
                                clock.UtcNow.AddTicks(sample + 1))))
                        {
                            throw new InvalidOperationException("Progress write-behind rejected an in-range task.");
                        }

                        versions[taskIndex] = targetVersion;
                        queuedSamples++;
                    }
                }

                await clock.ReleaseNextDelayAsync(cancellationToken).ConfigureAwait(false);
                await store
                    .WaitForWriteCountAsync(checked((second + 1) * taskCount), cancellationToken)
                    .ConfigureAwait(false);
                if (writeBehind.Completion.IsFaulted)
                {
                    await writeBehind.Completion.ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            var taskMinutes = taskCount * (simulatedSeconds / 60d);
            return new SystemBenchmarkResult(
                "sqlite_progress_write_rate",
                $"tasks={taskCount}; simulated_seconds={simulatedSeconds}; samples_per_task_second={samplesPerTaskSecond}",
                "sqlite-write-behind",
                Available: true,
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["source_progress_samples"] = queuedSamples,
                    ["sqlite_progress_writes"] = store.ProgressWriteCount,
                    ["writes_per_task_minute"] = store.ProgressWriteCount / taskMinutes,
                    ["wall_elapsed_milliseconds"] = stopwatch.Elapsed.TotalMilliseconds
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source_progress_samples"] = "count",
                    ["sqlite_progress_writes"] = "count",
                    ["writes_per_task_minute"] = "writes/task-minute",
                    ["wall_elapsed_milliseconds"] = "ms"
                },
                "A deterministic clock advances one second per flush; multiple source samples for a task are coalesced into one atomic SQLite progress write per interval.");
        }
        finally
        {
            innerStore.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class StepClock(DateTimeOffset utcNow) : IClock
    {
        private readonly Channel<DelayRequest> _delays = Channel.CreateUnbounded<DelayRequest>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private DateTimeOffset _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_delays.Writer.TryWrite(new DelayRequest(delay, completion)))
            {
                throw new InvalidOperationException("Benchmark clock delay queue is closed.");
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public async Task ReleaseNextDelayAsync(CancellationToken cancellationToken)
        {
            var request = await _delays.Reader
                .ReadAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            _utcNow += request.Delay;
            request.Completion.TrySetResult();
        }

        private sealed record DelayRequest(TimeSpan Delay, TaskCompletionSource Completion);
    }

    private sealed class CountingStore(IDownloadTaskStore inner) : IDownloadTaskStore
    {
        private readonly IDownloadTaskStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly Channel<byte> _writeSignals = Channel.CreateUnbounded<byte>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private long _progressWriteCount;

        public long ProgressWriteCount => Interlocked.Read(ref _progressWriteCount);

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            _inner.InitializeAsync(cancellationToken);

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken) =>
            _inner.AddAsync(task, cancellationToken);

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            _inner.UpdateAsync(task, expectedVersion, cancellationToken);

        public async Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken)
        {
            var result = await _inner.UpdateProgressAsync(progressWrite, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _progressWriteCount);
            _writeSignals.Writer.TryWrite(0);
            return result;
        }

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => _inner.FindAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
            _inner.GetUnfinishedAsync(cancellationToken);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            _inner.GetHistoryPageAsync(cursor, pageSize, cancellationToken);

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => _inner.DeleteAsync(taskId, cancellationToken);

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            _inner.ClearHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => _inner.GetQuarantinedRecordsAsync(cancellationToken);

        public async Task WaitForWriteCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (ProgressWriteCount < expectedCount)
            {
                await _writeSignals.Reader
                    .ReadAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
