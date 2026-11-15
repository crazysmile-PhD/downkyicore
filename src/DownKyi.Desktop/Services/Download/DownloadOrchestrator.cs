using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadOrchestrator : IDownloadRuntime
{
    internal static readonly TimeSpan WorkerShutdownTimeout = TimeSpan.FromSeconds(4);
    private readonly IDownloadTaskExecutor _executor;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskSchedulerService _tasks;
    private readonly int _workerCount;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly ConcurrentDictionary<DownloadTaskId, byte> _scheduledTasks = new();
    private readonly ConcurrentDictionary<DownloadTaskId, ActiveDownloadExecution> _activeExecutions = new();
    private readonly Lock _schedulerSync = new();
    private readonly SemaphoreSlim _schedulerIntentGate = new(1, 1);
    private readonly HashSet<DownloadTaskId> _pauseOwnedTasks = [];
    private readonly Queue<DownloadTaskId> _resumePriority = [];
    private Channel<DownloadTaskId>? _admissionQueue;
    private Channel<DownloadTaskId>? _downloadQueue;
    private Task _admissionWorker = Task.CompletedTask;
    private Task[] _downloadWorkers = [];
    private CancellationTokenSource? _tokenSource;
    private TaskCompletionSource? _dispatchBlocked;
    private TaskCompletionSource? _intentSuperseded;
    private long _schedulerIntentVersion;
    private bool _pauseIntent;
    private bool _disposed;

    public DownloadOrchestrator(
        IDownloadTaskExecutor executor,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskSchedulerService tasks,
        int workerCount,
        ILogger<DownloadOrchestrator> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _workerCount = Math.Max(1, workerCount);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tokenSource != null)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ResetSchedulerState();
        _tokenSource = new CancellationTokenSource();
        _admissionQueue = Channel.CreateUnbounded<DownloadTaskId>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _downloadQueue = Channel.CreateBounded<DownloadTaskId>(new BoundedChannelOptions(
            Math.Max(32, _workerCount * 8))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _workerCount == 1,
            SingleWriter = false
        });
        _downloadWorkers = Enumerable.Range(0, _workerCount)
            .Select(_ => DownloadWorkerAsync(_downloadQueue.Reader, _tokenSource.Token))
            .ToArray();
        _admissionWorker = ForwardAdmissionsAsync(
            _admissionQueue.Reader,
            _downloadQueue.Writer,
            _tokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task EnqueueAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var queue = _admissionQueue
            ?? throw new InvalidOperationException("The download runtime has not started.");
        if (!_scheduledTasks.TryAdd(taskId, 0))
        {
            return;
        }

        try
        {
            await queue.Writer.WriteAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _scheduledTasks.TryRemove(taskId, out _);
            throw;
        }
    }

    public async Task<bool> CancelAsync(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (!_activeExecutions.TryGetValue(taskId, out var execution))
        {
            return false;
        }

        await CancelAndWaitForCompletionAsync(execution)
            .WaitAsync(WorkerShutdownTimeout, CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    public Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var intent = BeginPauseIntent();
        return ApplyPauseIntentAsync(
            intent.Version,
            intent.Superseded,
            intent.ActiveExecutions,
            cancellationToken);
    }

    public Task ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var intent = BeginResumeIntent();
        return ApplyResumeIntentAsync(
            intent.Version,
            intent.Superseded,
            cancellationToken);
    }

    private (
        long Version,
        Task Superseded,
        KeyValuePair<DownloadTaskId, ActiveDownloadExecution>[] ActiveExecutions)
        BeginPauseIntent()
    {
        lock (_schedulerSync)
        {
            EnsureStarted();
            _intentSuperseded?.TrySetResult();
            _intentSuperseded = CreateSignal();
            _pauseIntent = true;
            _dispatchBlocked ??= CreateSignal();
            return (
                ++_schedulerIntentVersion,
                _intentSuperseded.Task,
                _activeExecutions.ToArray());
        }
    }

    private (long Version, Task Superseded) BeginResumeIntent()
    {
        lock (_schedulerSync)
        {
            EnsureStarted();
            _intentSuperseded?.TrySetResult();
            _intentSuperseded = CreateSignal();
            _pauseIntent = false;
            return (++_schedulerIntentVersion, _intentSuperseded.Task);
        }
    }

    private async Task ApplyPauseIntentAsync(
        long version,
        Task superseded,
        IReadOnlyList<KeyValuePair<DownloadTaskId, ActiveDownloadExecution>> activeExecutions,
        CancellationToken cancellationToken)
    {
        await _schedulerIntentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentIntent(version, pauseIntent: true))
            {
                return;
            }

            var pausedCompletions = new List<Task>(activeExecutions.Count);
            foreach (var (taskId, execution) in activeExecutions)
            {
                if (!IsCurrentIntent(version, pauseIntent: true))
                {
                    return;
                }

                var startedOrSuperseded = await Task
                    .WhenAny(execution.Started, superseded)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (ReferenceEquals(startedOrSuperseded, superseded)
                    || !await execution.Started.ConfigureAwait(false))
                {
                    continue;
                }

                var pause = await _tasks
                    .PauseAsync(taskId, cancellationToken)
                    .ConfigureAwait(false);
                if (!pause.TryGetValue(out var paused))
                {
                    if (pause.Error?.Code == "download.transition.invalid")
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "Download pause state could not be persisted.");
                }

                if (paused.Phase != DownloadPhase.Pausing)
                {
                    continue;
                }

                lock (_schedulerSync)
                {
                    _pauseOwnedTasks.Add(taskId);
                }

                pausedCompletions.Add(execution.Completion);
            }

            if (!IsCurrentIntent(version, pauseIntent: true)
                || pausedCompletions.Count == 0)
            {
                return;
            }

            var allPausedExecutionsStopped = Task.WhenAll(pausedCompletions);
            var completionOrSuperseded = await Task
                .WhenAny(allPausedExecutionsStopped, superseded)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ReferenceEquals(completionOrSuperseded, allPausedExecutionsStopped))
            {
                await allPausedExecutionsStopped.ConfigureAwait(false);
            }
        }
        finally
        {
            _schedulerIntentGate.Release();
        }
    }

    private async Task ApplyResumeIntentAsync(
        long version,
        Task superseded,
        CancellationToken cancellationToken)
    {
        await _schedulerIntentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentIntent(version, pauseIntent: false))
            {
                return;
            }

            DownloadTaskId[] pausedTaskIds;
            lock (_schedulerSync)
            {
                pausedTaskIds = [.. _pauseOwnedTasks];
            }

            foreach (var taskId in pausedTaskIds)
            {
                if (!IsCurrentIntent(version, pauseIntent: false))
                {
                    return;
                }

                if (_activeExecutions.TryGetValue(taskId, out var execution))
                {
                    var completionOrSuperseded = await Task
                        .WhenAny(execution.Completion, superseded)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (ReferenceEquals(completionOrSuperseded, superseded))
                    {
                        return;
                    }

                    await execution.Completion.ConfigureAwait(false);
                }

                if (!IsCurrentIntent(version, pauseIntent: false))
                {
                    return;
                }

                var task = await _tasks
                    .FindAsync(taskId, cancellationToken)
                    .ConfigureAwait(false);
                if (task?.Phase == DownloadPhase.Pausing)
                {
                    return;
                }

                if (task?.Phase == DownloadPhase.Paused)
                {
                    var resume = await _tasks
                        .ResumeAsync(taskId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!resume.TryGetValue(out task))
                    {
                        throw new InvalidOperationException(
                            "Download resume state could not be persisted.");
                    }

                    if (task.Phase != DownloadPhase.Queued)
                    {
                        return;
                    }
                }

                if (task?.Phase == DownloadPhase.Queued)
                {
                    EnqueuePauseOwnedTask(taskId);
                }

                lock (_schedulerSync)
                {
                    _pauseOwnedTasks.Remove(taskId);
                }
            }

            OpenDispatchIfCurrent(version);
        }
        finally
        {
            _schedulerIntentGate.Release();
        }
    }

    private bool IsCurrentIntent(long version, bool pauseIntent)
    {
        lock (_schedulerSync)
        {
            return version == _schedulerIntentVersion && _pauseIntent == pauseIntent;
        }
    }

    private void OpenDispatchIfCurrent(long version)
    {
        lock (_schedulerSync)
        {
            if (version != _schedulerIntentVersion || _pauseIntent)
            {
                return;
            }

            _dispatchBlocked?.TrySetResult();
            _dispatchBlocked = null;
        }
    }

    private void EnqueuePauseOwnedTask(DownloadTaskId taskId)
    {
        lock (_schedulerSync)
        {
            if (_scheduledTasks.TryAdd(taskId, 0))
            {
                _resumePriority.Enqueue(taskId);
            }
        }
    }

    private void EnsureStarted()
    {
        if (_tokenSource == null)
        {
            throw new InvalidOperationException("The download runtime has not started.");
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CancelAndWaitForCompletionAsync(
        ActiveDownloadExecution execution)
    {
        try
        {
            await execution.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            await execution.Completion.ConfigureAwait(false);
            return;
        }

        await execution.Completion.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_tokenSource == null)
        {
            return;
        }

        _admissionQueue?.Writer.TryComplete();
        try
        {
            await DownloadShutdownCoordinator.StopAsync(
                _tokenSource,
                [.. _downloadWorkers, _admissionWorker],
                WorkerShutdownTimeout,
                exception => _logger.LogErrorMessage(
                    "Download workers failed during shutdown.",
                    exception),
                _executor.PersistShutdownStateAsync).ConfigureAwait(false);
        }
        finally
        {
            _tokenSource.Dispose();
            _tokenSource = null;
            _admissionQueue = null;
            _downloadQueue = null;
            _admissionWorker = Task.CompletedTask;
            _downloadWorkers = [];
            _scheduledTasks.Clear();
            ResetSchedulerState();
        }
    }

    private static async Task ForwardAdmissionsAsync(
        ChannelReader<DownloadTaskId> admissions,
        ChannelWriter<DownloadTaskId> downloads,
        CancellationToken shutdownToken)
    {
        try
        {
            await foreach (var taskId in admissions.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                await downloads.WriteAsync(taskId, shutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task DownloadWorkerAsync(
        ChannelReader<DownloadTaskId> reader,
        CancellationToken shutdownToken)
    {
        try
        {
            while (true)
            {
                var admission = await WaitForExecutionAdmissionAsync(reader, shutdownToken)
                    .ConfigureAwait(false);
                if (admission == null)
                {
                    return;
                }

                var (taskId, execution) = admission.Value;
                try
                {
                    var task = await _tasks.FindAsync(taskId, shutdownToken).ConfigureAwait(false);
                    if (task?.Phase != DownloadPhase.Queued)
                    {
                        continue;
                    }

                    await _stateWriter.StartAsync(taskId, execution.Token).ConfigureAwait(false);
                    execution.ReportStarted();
                    await _executor.ExecuteAsync(taskId, execution.Token).ConfigureAwait(false);
                }
                catch (DownloadPausedException)
                {
                    // Pause is confirmed in finally, after the pipeline has drained.
                    continue;
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested)
                {
                    continue;
                }
                catch (OperationCanceledException exception)
                {
                    _logger.LogErrorMessage(
                        "Download worker observed cancellation while its owning token remained active.",
                        exception);
                    await TryMarkFailedAsync(taskId).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or FormatException
                    or NotSupportedException or TimeoutException or HttpRequestException
                    or Newtonsoft.Json.JsonException or SqliteException)
                {
                    _logger.LogErrorMessage("Download worker failed.", exception);
                    await TryMarkFailedAsync(taskId).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        execution.ReportNotStarted();
                        await ConfirmPauseAfterWorkerStopsAsync(taskId).ConfigureAwait(false);
                        ActiveDownloadExecution? ownedExecution = null;
                        lock (_schedulerSync)
                        {
                            _activeExecutions.TryRemove(taskId, out ownedExecution);
                        }

                        if (ownedExecution != null)
                        {
                            ownedExecution.Dispose();
                        }
                        else
                        {
                            execution.Dispose();
                        }

                        _scheduledTasks.TryRemove(taskId, out _);
                        await RequeueIfNeededAsync(taskId, shutdownToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        execution?.Complete();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task<(DownloadTaskId TaskId, ActiveDownloadExecution Execution)?>
        WaitForExecutionAdmissionAsync(
        ChannelReader<DownloadTaskId> reader,
        CancellationToken shutdownToken)
    {
        while (true)
        {
            Task? dispatchResumed;
            lock (_schedulerSync)
            {
                dispatchResumed = _dispatchBlocked?.Task;
                if (dispatchResumed == null)
                {
                    DownloadTaskId? taskId = null;
                    if (_resumePriority.TryDequeue(out var resumedTaskId))
                    {
                        taskId = resumedTaskId;
                    }
                    else if (reader.TryRead(out var queuedTaskId))
                    {
                        taskId = queuedTaskId;
                    }

                    if (taskId == null)
                    {
                        dispatchResumed = null;
                    }
                    else
                    {
                        var execution = new ActiveDownloadExecution(shutdownToken);
                        if (_activeExecutions.TryAdd(taskId, execution))
                        {
                            return (taskId, execution);
                        }

                        execution.Dispose();
                        _scheduledTasks.TryRemove(taskId, out _);
                        continue;
                    }
                }
            }

            if (dispatchResumed != null)
            {
                await dispatchResumed.WaitAsync(shutdownToken).ConfigureAwait(false);
                continue;
            }

            if (!await reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private async Task TryMarkFailedAsync(DownloadTaskId taskId)
    {
        try
        {
            await _executor.MarkFailedAsync(taskId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
        {
            _logger.LogErrorMessage("Download failure state could not be persisted.", exception);
        }
    }

    private async Task ConfirmPauseAfterWorkerStopsAsync(DownloadTaskId taskId)
    {
        try
        {
            await _stateWriter.ConfirmPausedIfPausingAsync(taskId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogErrorMessage("Download pause acknowledgement failed.", exception);
        }
    }

    private async Task RequeueIfNeededAsync(
        DownloadTaskId taskId,
        CancellationToken shutdownToken)
    {
        if (shutdownToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var task = await _tasks.FindAsync(taskId, shutdownToken).ConfigureAwait(false);
            if (task?.Phase == DownloadPhase.Queued)
            {
                await EnqueueAsync(taskId, shutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }
        catch (ChannelClosedException)
        {
            return;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetSchedulerState();
        _tokenSource?.Cancel();
        _tokenSource?.Dispose();
        _tokenSource = null;
        _schedulerIntentGate.Dispose();
    }

    private void ResetSchedulerState()
    {
        lock (_schedulerSync)
        {
            _intentSuperseded?.TrySetResult();
            _intentSuperseded = null;
            _dispatchBlocked?.TrySetResult();
            _dispatchBlocked = null;
            _pauseOwnedTasks.Clear();
            _resumePriority.Clear();
            _pauseIntent = false;
            _schedulerIntentVersion++;
        }
    }

    private sealed class ActiveDownloadExecution : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveDownloadExecution(CancellationToken shutdownToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        }

        public CancellationToken Token => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public Task Completion => _completion.Task;

        public Task<bool> Started => _started.Task;

        public Task CancelAsync() => _cancellation.CancelAsync();

        public void ReportStarted() => _started.TrySetResult(true);

        public void ReportNotStarted() => _started.TrySetResult(false);

        public void Complete() => _completion.TrySetResult();

        public void Dispose() => _cancellation.Dispose();
    }
}
