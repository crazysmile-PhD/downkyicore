using System;
using System.Collections.Concurrent;
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
    private readonly IDownloadTaskExecutor _executor;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly int _workerCount;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly ConcurrentDictionary<DownloadTaskId, byte> _scheduledTasks = new();
    private readonly ConcurrentDictionary<DownloadTaskId, CancellationTokenSource> _activeExecutions = new();
    private Channel<DownloadTaskId>? _admissionQueue;
    private Channel<DownloadTaskId>? _downloadQueue;
    private Task _admissionWorker = Task.CompletedTask;
    private Task[] _downloadWorkers = [];
    private CancellationTokenSource? _tokenSource;
    private bool _disposed;

    public DownloadOrchestrator(
        IDownloadTaskExecutor executor,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskApplicationService tasks,
        int workerCount,
        ILogger<DownloadOrchestrator> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _workerCount = Math.Max(1, workerCount);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tokenSource != null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _executor.StartAsync(cancellationToken).ConfigureAwait(false);

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

        try
        {
            await execution.CancelAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
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
                TimeSpan.FromSeconds(30),
                exception => _logger.LogErrorMessage(
                    "Download workers failed during shutdown.",
                    exception),
                _executor.PersistShutdownStateAsync).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _executor.StopAsync(cancellationToken).ConfigureAwait(false);
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
            }
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
            await foreach (var taskId in reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                CancellationTokenSource? execution = null;
                var ownsExecution = false;
                try
                {
                    var task = await _tasks.FindAsync(taskId, shutdownToken).ConfigureAwait(false);
                    if (task?.Phase != DownloadPhase.Queued)
                    {
                        continue;
                    }

                    execution = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    ownsExecution = _activeExecutions.TryAdd(taskId, execution);
                    if (!ownsExecution)
                    {
                        continue;
                    }

                    await _stateWriter.StartAsync(taskId, execution.Token).ConfigureAwait(false);
                    await _executor.ExecuteAsync(taskId, execution.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (execution?.IsCancellationRequested == true)
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
                    await ConfirmPauseAfterWorkerStopsAsync(taskId).ConfigureAwait(false);
                    if (ownsExecution &&
                        _activeExecutions.TryRemove(taskId, out var ownedExecution))
                    {
                        ownedExecution.Dispose();
                    }
                    else
                    {
                        execution?.Dispose();
                    }

                    _scheduledTasks.TryRemove(taskId, out _);
                    await RequeueIfNeededAsync(taskId, shutdownToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task TryMarkFailedAsync(DownloadTaskId taskId)
    {
        try
        {
            var task = await _tasks.FindAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            if (task?.Phase is not (DownloadPhase.Queued or DownloadPhase.Downloading))
            {
                return;
            }

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
            var task = await _tasks.FindAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            if (task?.Phase == DownloadPhase.Pausing)
            {
                await _stateWriter.ConfirmPausedAsync(taskId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
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
        _tokenSource?.Cancel();
        _tokenSource?.Dispose();
        _tokenSource = null;
        _executor.Dispose();
    }
}
