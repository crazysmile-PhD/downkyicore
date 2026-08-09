using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Platform;
using DownKyi.Services.Download;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadBootstrapHostedServiceTests
{
    [Fact]
    public async Task HostLifecycleOwnsDownloadRuntimeAndUiProjection()
    {
        using var runtime = new RecordingDownloadRuntime();
        var dispatcher = new ImmediateUiDispatcher();
        var listState = new DownloadListState();
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(new EmptyDownloadTaskStore(), clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        var stateWriter = new DownloadTaskStateWriter(tasks);
        var queueGateway = new DownloadTaskQueueGateway();
        using var service = new DownloadBootstrapHostedService(
            listState,
            storage,
            stateWriter,
            new RecordingRuntimeFactory(runtime),
            queueGateway,
            dispatcher,
            NullLogger<DownloadBootstrapHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.Started);
        Assert.True(runtime.Ended);
        Assert.True(dispatcher.InvocationCount >= 2);
        Assert.Empty(listState.Downloading);
        Assert.Empty(listState.Downloaded);
    }

    [Fact]
    public async Task StartupQueuesPersistedAndInterruptedTasksWithoutUiPolling()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-bootstrap-queue-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(Path.Combine(directory, "download.db")),
                new SystemClock());
            var clock = new SystemClock();
            using var tasks = new DownloadTaskApplicationService(store, clock);
            using var projections = new DownloadTaskProjectionStore(tasks, clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var queued = CreateTask("queued");
            var interrupted = CreateTask("interrupted");
            Assert.True((await tasks.AddAsync(
                queued,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await tasks.AddAsync(
                interrupted,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await tasks.StartAsync(
                interrupted.Id,
                TestContext.Current.CancellationToken)).IsSuccess);
            using var runtime = new RecordingDownloadRuntime();
            var queueGateway = new DownloadTaskQueueGateway();
            using var service = new DownloadBootstrapHostedService(
                new DownloadListState(),
                projections,
                stateWriter,
                new RecordingRuntimeFactory(runtime),
                queueGateway,
                new ImmediateUiDispatcher(),
                NullLogger<DownloadBootstrapHostedService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                [interrupted.Id, queued.Id],
                runtime.Enqueued.OrderBy(taskId => taskId.Value, StringComparer.Ordinal));
            var recovered = Assert.IsType<DownloadTask>(
                await tasks.FindAsync(interrupted.Id, TestContext.Current.CancellationToken));
            Assert.Equal(DownloadPhase.Queued, recovered.Phase);
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupAdmissionFailureCleansUpPartiallyStartedRuntime()
    {
        using var runtime = new RecordingDownloadRuntime(failOnEnqueue: true);
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(
            new EmptyDownloadTaskStore([CreateTask("queued")]),
            clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        using var service = new DownloadBootstrapHostedService(
            new DownloadListState(),
            storage,
            new DownloadTaskStateWriter(tasks),
            new RecordingRuntimeFactory(runtime),
            new DownloadTaskQueueGateway(),
            new ImmediateUiDispatcher(),
            NullLogger<DownloadBootstrapHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.Started);
        Assert.True(runtime.Ended);
        Assert.True(runtime.Disposed);
    }

    private static DownloadTask CreateTask(string id)
    {
        return DownloadTask.Create(
            new DownloadTaskId(id),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity($"BV-{id}", 1, 2, 0, 1, 1),
                "title",
                id,
                "00:01",
                "avc1",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty,
                string.Empty,
                0),
            new DownloadPlan([], [], 0),
            new DownloadOutput(id, null),
            DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingRuntimeFactory(IDownloadRuntime runtime) : IDownloadRuntimeFactory
    {
        public IDownloadRuntime Create()
        {
            return runtime;
        }
    }

    private sealed class RecordingDownloadRuntime(bool failOnEnqueue = false) : IDownloadRuntime
    {
        public List<DownloadTaskId> Enqueued { get; } = [];

        public bool Started { get; private set; }

        public bool Ended { get; private set; }

        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ended = true;
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failOnEnqueue)
            {
                throw new InvalidOperationException("Synthetic startup admission failure.");
            }

            Enqueued.Add(taskId);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId)
        {
            return Task.FromResult(false);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class EmptyDownloadTaskStore(
        IReadOnlyList<DownloadTask>? unfinished = null) : IDownloadTaskStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            return Task.FromResult<DownloadTask?>(null);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(unfinished ?? (IReadOnlyList<DownloadTask>)Array.Empty<DownloadTask>());
        }

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DownloadHistoryPage(Array.Empty<DownloadTask>(), null));
        }

        public Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());
        }
    }
}
