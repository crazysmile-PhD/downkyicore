using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
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
    public async Task StopAsyncWaitsForOwnedRuntimeToBecomeQuiescent()
    {
        using var runtime = new BlockingDownloadRuntime();
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(new EmptyDownloadTaskStore(), clock);
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
        var stopTask = service.StopAsync(TestContext.Current.CancellationToken);
        await runtime.StopEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(stopTask.IsCompleted);
        Assert.False(runtime.IsQuiescent);

        runtime.AllowStop.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(runtime.IsQuiescent);
    }

    [Fact]
    public async Task StartupQueuesPersistedAndInterruptedTasksWithoutUiPolling()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-bootstrap-queue-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "download.db");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
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
            ClearOwnedSqlitePool(databasePath);
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

    [Fact]
    public async Task TerminalBootstrapFailureDoesNotAcceptNewPendingTasks()
    {
        using var runtime = new RecordingDownloadRuntime(failOnEnqueue: true);
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(
            new EmptyDownloadTaskStore([CreateTask("startup-task")]),
            clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queueGateway = new DownloadTaskQueueGateway();
        using var service = new DownloadBootstrapHostedService(
            listState,
            storage,
            new DownloadTaskStateWriter(tasks),
            new RecordingRuntimeFactory(runtime),
            queueGateway,
            new ImmediateUiDispatcher(),
            NullLogger<DownloadBootstrapHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        var exception = await Record.ExceptionAsync(() => queueGateway.EnqueueAsync(
            new DownloadTaskId("admitted-after-terminal-failure"),
            TestContext.Current.CancellationToken));
        var startupTask = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            new DownloadTaskId("startup-task"),
            TestContext.Current.CancellationToken));

        Assert.IsType<DownloadRuntimeUnavailableException>(exception);
        Assert.Equal(DownloadPhase.Failed, startupTask.Phase);
        Assert.Equal("download.runtime.unavailable", startupTask.Failure?.Code);
        Assert.Equal(
            DownloadStatus.DownloadFailed,
            Assert.Single(listState.Downloading).Downloading.DownloadStatus);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("win32")]
    [InlineData("task-canceled")]
    [InlineData("unexpected")]
    public async Task RuntimeStartupFailureFaultsGateway(string failureKind)
    {
        using var runtime = new RecordingDownloadRuntime(
            startFailure: CreateRuntimeStartupFailure(failureKind));
        var clock = new FixedClock();
        var startupTask = CreateTask($"startup-{failureKind}");
        using var tasks = new DownloadTaskApplicationService(
            new EmptyDownloadTaskStore([startupTask]),
            clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        var queueGateway = new DownloadTaskQueueGateway();
        using var service = new DownloadBootstrapHostedService(
            new DownloadListState(),
            storage,
            new DownloadTaskStateWriter(tasks),
            new RecordingRuntimeFactory(runtime),
            queueGateway,
            new ImmediateUiDispatcher(),
            NullLogger<DownloadBootstrapHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() =>
            queueGateway.EnqueueAsync(
                new DownloadTaskId($"after-{failureKind}-failure"),
                TestContext.Current.CancellationToken));
        var persisted = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            startupTask.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Failed, persisted.Phase);
        Assert.Equal("download.runtime.unavailable", persisted.Failure?.Code);
        Assert.True(runtime.Ended);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task TerminalBootstrapFailurePreservesNonRunnableStartupTasks()
    {
        var transitionTime = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var paused = CreateTask("paused").Pause(transitionTime).RequireValue();
        var failed = CreateTask("failed").Fail(
            new DownloadFailure("download.existing", "Existing failure.", true),
            transitionTime).RequireValue();
        using var runtime = new RecordingDownloadRuntime(
            startFailure: new TimeoutException("Synthetic runtime readiness timeout."));
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(
            new EmptyDownloadTaskStore([paused, failed]),
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

        var persistedPaused = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            paused.Id,
            TestContext.Current.CancellationToken));
        var persistedFailed = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            failed.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Paused, persistedPaused.Phase);
        Assert.Equal(DownloadPhase.Failed, persistedFailed.Phase);
        Assert.Equal("download.existing", persistedFailed.Failure?.Code);
    }

    private static Exception CreateRuntimeStartupFailure(string failureKind) => failureKind switch
    {
        "timeout" => new TimeoutException("Synthetic runtime readiness timeout."),
        "http" => new HttpRequestException("Synthetic runtime RPC failure."),
        "json" => new Newtonsoft.Json.JsonSerializationException(
            "Synthetic runtime RPC response failure."),
        "win32" => new System.ComponentModel.Win32Exception(
            "Synthetic operating system process startup failure."),
        "task-canceled" => new TaskCanceledException(
            "Synthetic HttpClient timeout."),
        "unexpected" => new FormatException(
            "Synthetic failure outside the former exception allowlist."),
        _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null)
    };

    [Fact]
    public async Task TerminalBootstrapFailurePreservesTaskPausedAfterStartupSnapshot()
    {
        var task = CreateTask("paused-during-startup");
        using var runtime = new BlockingFailingStartRuntime();
        var clock = new FixedClock();
        using var tasks = new DownloadTaskApplicationService(
            new EmptyDownloadTaskStore([task]),
            clock);
        using var storage = new DownloadTaskProjectionStore(tasks, clock);
        var stateWriter = new DownloadTaskStateWriter(tasks);
        var listState = new DownloadListState();
        using var service = new DownloadBootstrapHostedService(
            listState,
            storage,
            stateWriter,
            new RecordingRuntimeFactory(runtime),
            new DownloadTaskQueueGateway(),
            new ImmediateUiDispatcher(),
            NullLogger<DownloadBootstrapHostedService>.Instance);

        var startup = service.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stateWriter.PauseAsync(task.Id, TestContext.Current.CancellationToken);
        runtime.FailStartup.TrySetResult();
        await startup.ConfigureAwait(true);

        var persisted = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            task.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Paused, persisted.Phase);
        Assert.Equal(
            DownloadStatus.Pause,
            Assert.Single(listState.Downloading).Downloading.DownloadStatus);
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
            new DownloadPlan(DownloadContentSelection.None, [], 0, nfoRequest: null),
            new DownloadOutput(id, null),
            DateTimeOffset.UnixEpoch);
    }

    private static void ClearOwnedSqlitePool(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private sealed class RecordingRuntimeFactory(IDownloadRuntime runtime) : IDownloadRuntimeFactory
    {
        public IDownloadRuntime Create()
        {
            return runtime;
        }
    }

    private sealed class RecordingDownloadRuntime(
        bool failOnEnqueue = false,
        Exception? startFailure = null) : IDownloadRuntime
    {
        public List<DownloadTaskId> Enqueued { get; } = [];

        public bool Started { get; private set; }

        public bool Ended { get; private set; }

        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (startFailure != null)
            {
                throw startFailure;
            }

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

    private sealed class BlockingDownloadRuntime : IDownloadRuntime
    {
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsQuiescent { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopEntered.TrySetResult();
            await AllowStop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            IsQuiescent = true;
        }

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId)
        {
            return Task.FromResult(false);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingFailingStartRuntime : IDownloadRuntime
    {
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailStartup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult();
            await FailStartup.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new TimeoutException("Synthetic delayed runtime startup failure.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId) => Task.FromResult(false);

        public void Dispose()
        {
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
        private readonly Dictionary<DownloadTaskId, DownloadTask> _tasks =
            (unfinished ?? []).ToDictionary(task => task.Id);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            _tasks[task.Id] = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            _tasks[task.Id] = task;
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
            _tasks.TryGetValue(taskId, out var task);
            return Task.FromResult(task);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DownloadTask>>([.. _tasks.Values]);
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
            _tasks.Remove(taskId);
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
