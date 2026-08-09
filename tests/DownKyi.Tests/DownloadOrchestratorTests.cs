using System.Collections.Concurrent;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadOrchestratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task EventDrivenWorkersExecuteEachTaskExactlyOnce(int workerCount)
    {
        using var context = new OrchestratorContext();
        DownloadTaskId[] taskIds = await context.AddQueuedTasksAsync(48);
        var executions = new ConcurrentDictionary<DownloadTaskId, int>();
        var allExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = new RecordingExecutor((taskId, _) =>
        {
            executions.AddOrUpdate(taskId, 1, static (_, count) => count + 1);
            if (executions.Count == taskIds.Length)
            {
                allExecuted.TrySetResult();
            }

            return Task.CompletedTask;
        });
        using var orchestrator = context.CreateOrchestrator(executor, workerCount);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        foreach (var taskId in taskIds)
        {
            await orchestrator.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
            await orchestrator.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
        }

        await allExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(taskIds.Length, executions.Count);
        Assert.All(executions.Values, count => Assert.Equal(1, count));
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AdmissionDoesNotWaitForBoundedWorkerQueueCapacity()
    {
        using var context = new OrchestratorContext();
        DownloadTaskId[] taskIds = await context.AddQueuedTasksAsync(96);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorkers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = new RecordingExecutor(async (_, cancellationToken) =>
        {
            firstStarted.TrySetResult();
            await releaseWorkers.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        });
        using var orchestrator = context.CreateOrchestrator(executor, workerCount: 1);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(taskIds.Select(taskId =>
                orchestrator.EnqueueAsync(taskId, TestContext.Current.CancellationToken)))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        releaseWorkers.TrySetResult();
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelingOneActiveTaskDoesNotStopTheWorker()
    {
        using var context = new OrchestratorContext();
        DownloadTaskId[] taskIds = await context.AddQueuedTasksAsync(2);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = new RecordingExecutor(async (taskId, cancellationToken) =>
        {
            if (taskId == taskIds[0])
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return;
            }

            secondExecuted.TrySetResult();
        });
        using var orchestrator = context.CreateOrchestrator(executor, workerCount: 1);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        await orchestrator.EnqueueAsync(taskIds[0], TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(await orchestrator.CancelAsync(taskIds[0]));
        await orchestrator.EnqueueAsync(taskIds[1], TestContext.Current.CancellationToken);
        await secondExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelingOneConcurrentTaskDoesNotCancelAnotherTasksToken()
    {
        using var context = new OrchestratorContext();
        DownloadTaskId[] taskIds = await context.AddQueuedTasksAsync(2);
        var started = new ConcurrentDictionary<DownloadTaskId, CancellationToken>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = new RecordingExecutor(async (taskId, cancellationToken) =>
        {
            started[taskId] = cancellationToken;
            if (started.Count == taskIds.Length)
            {
                bothStarted.TrySetResult();
            }

            if (taskId == taskIds[0])
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return;
            }

            await secondMayFinish.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        });
        using var orchestrator = context.CreateOrchestrator(executor, workerCount: 2);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        await orchestrator.EnqueueAsync(taskIds[0], TestContext.Current.CancellationToken);
        await orchestrator.EnqueueAsync(taskIds[1], TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(await orchestrator.CancelAsync(taskIds[0]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, started[taskIds[0]]));
        Assert.False(started[taskIds[1]].IsCancellationRequested);

        secondMayFinish.TrySetResult();
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShutdownRecoversActiveTaskToQueuedState()
    {
        using var context = new OrchestratorContext();
        DownloadTaskId taskId = Assert.Single(await context.AddQueuedTasksAsync(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = new RecordingExecutor(
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            },
            async () =>
            {
                var task = await context.Tasks.FindAsync(taskId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (task?.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing)
                {
                    await context.StateWriter
                        .RecoverInterruptedAsync(taskId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            });
        using var orchestrator = context.CreateOrchestrator(executor, workerCount: 1);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        await orchestrator.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);

        var restored = Assert.IsType<DownloadTask>(
            await context.Tasks.FindAsync(taskId, TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Queued, restored.Phase);
    }

    private sealed class OrchestratorContext : IDisposable
    {
        private readonly InMemoryDownloadTaskStore _store = new();

        public OrchestratorContext()
        {
            Tasks = new DownloadTaskApplicationService(_store, new SystemClock());
            StateWriter = new DownloadTaskStateWriter(Tasks);
        }

        public DownloadTaskApplicationService Tasks { get; }

        public DownloadTaskStateWriter StateWriter { get; }

        public async Task<DownloadTaskId[]> AddQueuedTasksAsync(int count)
        {
            var taskIds = new DownloadTaskId[count];
            for (var index = 0; index < count; index++)
            {
                var taskId = new DownloadTaskId($"queue-{index}");
                taskIds[index] = taskId;
                var task = DownloadTask.Create(
                    taskId,
                    new DownloadTaskMetadata(
                        new DownloadMediaIdentity($"BV{index}", index, index, 0, index, index),
                        "title",
                        $"part-{index}",
                        "00:01",
                        "avc1",
                        new DownloadQuality(80, "1080P"),
                        new DownloadQuality(30280, "AAC"),
                        string.Empty,
                        string.Empty,
                        0),
                    new DownloadPlan([], [], 0),
                    new DownloadOutput($"task-{index}", null),
                    DateTimeOffset.UnixEpoch);
                Assert.True((await Tasks.AddAsync(
                        task,
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(false)).IsSuccess);
            }

            return taskIds;
        }

        public DownloadOrchestrator CreateOrchestrator(
            IDownloadTaskExecutor executor,
            int workerCount)
        {
            return new DownloadOrchestrator(
                executor,
                StateWriter,
                Tasks,
                workerCount,
                NullLogger<DownloadOrchestrator>.Instance);
        }

        public void Dispose()
        {
            Tasks.Dispose();
        }
    }

    private sealed class RecordingExecutor(
        Func<DownloadTaskId, CancellationToken, Task> execute,
        Func<Task>? persist = null) : IDownloadTaskExecutor
    {
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            return execute(taskId, cancellationToken);
        }

        public Task MarkFailedAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PersistShutdownStateAsync()
        {
            return persist?.Invoke() ?? Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryDownloadTaskStore : IDownloadTaskStore
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<DownloadTaskId, DownloadTask> _tasks = [];

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryAdd(task.Id, task))
                {
                    return Task.FromResult(OperationResult.Failure(new OperationError(
                        "download.store.conflict",
                        "Task already exists.",
                        OperationErrorKind.Conflict)));
                }
            }

            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(task.Id, out var current) ||
                    current.Version != expectedVersion)
                {
                    return Task.FromResult(OperationResult.Failure(new OperationError(
                        "download.store.conflict",
                        "Task version changed.",
                        OperationErrorKind.Conflict)));
                }

                if (task.Phase == DownloadPhase.Deleted)
                {
                    _tasks.Remove(task.Id);
                }
                else
                {
                    _tasks[task.Id] = task;
                }
            }

            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _tasks.TryGetValue(taskId, out var task);
                return Task.FromResult(task);
            }
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<DownloadTask>>(
                    _tasks.Values.Where(task => task.Phase != DownloadPhase.Completed).ToArray());
            }
        }

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            lock (_sync)
            {
                return Task.FromResult(_tasks.Values.Any(task =>
                    task.Phase != DownloadPhase.Completed &&
                    task.Output.BasePath.Equals(basePath, comparison)));
            }
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DownloadHistoryPage([], null));
        }

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _tasks.Remove(taskId);
            }

            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success());
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
        }
    }
}
