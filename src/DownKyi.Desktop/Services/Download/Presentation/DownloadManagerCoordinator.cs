using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal enum DownloadArtifactOpenResult
{
    Opened,
    NoPublishedArtifactRecord,
    NotFound,
    OpenFailed
}

internal interface IDownloadManagerCoordinator
{
    Task PauseAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ResumeAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ToggleAsync(DownloadingItem item, CancellationToken cancellationToken = default);

    Task DeleteAsync(DownloadingItem item, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ClearDownloadedAsync(CancellationToken cancellationToken = default);

    Task LoadDownloadedHistoryAsync();

    Task RemoveDownloadedAsync(DownloadedItem item, CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenVideoAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenFolderAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);
}

internal sealed class DownloadManagerCoordinator : IDownloadManagerCoordinator, IDisposable, IAsyncDisposable
{
    private readonly ApplicationCancellation _applicationCancellation;
    private readonly DownloadTaskProjectionStore _storage;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly DownloadCommittedTaskDispatcher _committedTaskDispatcher;
    private readonly IDownloadSchedulerControl _schedulerControl;
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly DownloadTaskFileService _fileService;
    private readonly DownloadListState _downloadLists;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _downloadedHistoryLoadGate = new();
    private Task<IReadOnlyList<DownloadedItem>>? _downloadedHistoryLoadTask;
    private int _disposed;

    public DownloadManagerCoordinator(
        ApplicationCancellation applicationCancellation,
        DownloadTaskProjectionStore storage,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskQueue taskQueue,
        DownloadCommittedTaskDispatcher committedTaskDispatcher,
        IDownloadSchedulerControl schedulerControl,
        IDownloadRuntimeAvailability runtimeAvailability,
        DownloadTaskFileService fileService,
        DownloadListState downloadLists,
        IPlatformLauncher platformLauncher,
        IUiDispatcher uiDispatcher)
    {
        _applicationCancellation = applicationCancellation
            ?? throw new ArgumentNullException(nameof(applicationCancellation));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _committedTaskDispatcher = committedTaskDispatcher
            ?? throw new ArgumentNullException(nameof(committedTaskDispatcher));
        _schedulerControl = schedulerControl
            ?? throw new ArgumentNullException(nameof(schedulerControl));
        _runtimeAvailability = runtimeAvailability
            ?? throw new ArgumentNullException(nameof(runtimeAvailability));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public Task PauseAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _schedulerControl.PauseAllAsync(cancellationToken);
    }

    public Task ResumeAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _schedulerControl.ResumeAllAsync(cancellationToken);
    }

    public async Task ToggleAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var taskId = GetTaskId(item);
        if (item.Downloading.DownloadStatus is DownloadStatus.PauseStarted
            or DownloadStatus.Pause
            or DownloadStatus.DownloadFailed)
        {
            await ResumeAndEnqueueAsync(taskId, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (item.Downloading.DownloadStatus is DownloadStatus.NotStarted
            or DownloadStatus.WaitForDownload
            or DownloadStatus.Downloading)
        {
            await _stateWriter.PauseAsync(taskId, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task DeleteAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var taskId = GetTaskId(item);
        if (_storage.GetRequiredSnapshot(taskId).Phase != DownloadPhase.Canceled)
        {
            await _stateWriter.CancelAsync(taskId, cancellationToken).ConfigureAwait(true);
        }

        await _taskQueue.CancelAsync(taskId).ConfigureAwait(true);

        await DeleteCanceledAsync(item, taskId).ConfigureAwait(true);
    }

    private async Task DeleteCanceledAsync(DownloadingItem item, DownloadTaskId taskId)
    {

        // Once physical deletion starts, finish the database/list transaction even if app shutdown is requested.
        var deletion = await _fileService
            .DeleteGeneratedFilesAsync(item, _storage.GetRequiredSnapshot(taskId), CancellationToken.None)
            .ConfigureAwait(true);
        if (!deletion.Succeeded)
        {
            throw new IOException("One or more generated download files could not be deleted.");
        }

        await _stateWriter.DeleteAsync(taskId, CancellationToken.None).ConfigureAwait(true);
        _downloadLists.RemoveDownloading(item);
    }

    public async Task DeleteAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var prepared = new List<(DownloadingItem Item, DownloadTaskId TaskId)>();
        var failures = new List<Exception>();
        foreach (var item in items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskId = GetTaskId(item);
            try
            {
                if (_storage.GetRequiredSnapshot(taskId).Phase != DownloadPhase.Canceled)
                {
                    await _stateWriter.CancelAsync(taskId, cancellationToken).ConfigureAwait(true);
                }

                prepared.Add((item, taskId));
            }
            catch (Exception exception) when (IsDeleteFailure(exception))
            {
                failures.Add(exception);
            }
        }

        var pending = prepared
            .Select(deletion => new PendingDeletion(
                deletion.Item,
                deletion.TaskId,
                CancelRuntimeAsync(deletion.TaskId)))
            .ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Select(deletion => deletion.Cancellation))
                .ConfigureAwait(true);
            var deletion = pending.First(candidate => ReferenceEquals(candidate.Cancellation, completed));
            pending.Remove(deletion);
            try
            {
                await deletion.Cancellation.ConfigureAwait(true);
                await DeleteCanceledAsync(deletion.Item, deletion.TaskId).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsDeleteFailure(exception))
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new IOException(
                $"{failures.Count} download task(s) could not be deleted.",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }

    private async Task CancelRuntimeAsync(DownloadTaskId taskId)
    {
        await _taskQueue.CancelAsync(taskId).ConfigureAwait(true);
    }

    private static bool IsDeleteFailure(Exception exception) =>
        exception is InvalidOperationException or IOException or TimeoutException;

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        await _storage.ClearDownloadedAsync(cancellationToken).ConfigureAwait(true);
        _downloadLists.ClearDownloaded();
    }

    public async Task LoadDownloadedHistoryAsync()
    {
        Task<IReadOnlyList<DownloadedItem>> loadTask;
        lock (_downloadedHistoryLoadGate)
        {
            if (_downloadLists.IsDownloadedHistoryLoaded)
            {
                return;
            }

            if (_downloadedHistoryLoadTask != null)
            {
                loadTask = _downloadedHistoryLoadTask;
            }
            else
            {
                loadTask = LoadDownloadedHistoryCoreAsync();
                _downloadedHistoryLoadTask = loadTask;
                _ = loadTask.ContinueWith(
                    completed => ResetDownloadedHistoryLoadAfterFailure(completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        var items = await loadTask.ConfigureAwait(false);
        var cancellationToken = _applicationCancellation.ShutdownToken;
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _downloadLists.LoadDownloadedHistory(items);
            }
        }).ConfigureAwait(false);
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _storage.RemoveDownloadedAsync(item, cancellationToken).ConfigureAwait(true);
        _downloadLists.RemoveDownloaded(item);
    }

    public async Task<DownloadArtifactOpenResult> OpenVideoAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.DownloadBase == null)
        {
            return DownloadArtifactOpenResult.NotFound;
        }

        var artifacts = item.HistoryRecord.PublishedArtifacts;
        if (artifacts.Count == 0)
        {
            return DownloadArtifactOpenResult.NoPublishedArtifactRecord;
        }

        return artifacts.TryGetValue("media", out var path) && File.Exists(path)
            ? await _platformLauncher.OpenFileAsync(path, cancellationToken).ConfigureAwait(true)
                ? DownloadArtifactOpenResult.Opened : DownloadArtifactOpenResult.OpenFailed
            : DownloadArtifactOpenResult.NotFound;
    }

    public async Task<DownloadArtifactOpenResult> OpenFolderAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.DownloadBase == null)
        {
            return DownloadArtifactOpenResult.NotFound;
        }

        var artifacts = item.HistoryRecord.PublishedArtifacts;
        if (artifacts.Count == 0)
        {
            return DownloadArtifactOpenResult.NoPublishedArtifactRecord;
        }

        foreach (var candidate in artifacts.Values)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(candidate));
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            return await _platformLauncher.OpenFolderAsync(directory, cancellationToken)
                .ConfigureAwait(true)
                ? DownloadArtifactOpenResult.Opened
                : DownloadArtifactOpenResult.OpenFailed;
        }

        return DownloadArtifactOpenResult.NotFound;
    }

    private async Task ResumeAndEnqueueAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        _runtimeAvailability.EnsureAcceptingTasks();
        var resumed = await _stateWriter.ResumeAsync(taskId, cancellationToken).ConfigureAwait(true);
        await _committedTaskDispatcher.EnqueueCommittedAsync(resumed.Id).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<DownloadedItem>> LoadDownloadedHistoryCoreAsync()
    {
        var cancellationToken = _applicationCancellation.ShutdownToken;
        try
        {
            return await _storage
                .GetDownloadedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private void ResetDownloadedHistoryLoadAfterFailure(Task<IReadOnlyList<DownloadedItem>> completed)
    {
        if (completed.IsCompletedSuccessfully)
        {
            return;
        }

        lock (_downloadedHistoryLoadGate)
        {
            if (ReferenceEquals(_downloadedHistoryLoadTask, completed))
            {
                _downloadedHistoryLoadTask = null;
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    public async ValueTask DisposeAsync()
    {
        Task? historyLoadTask;
        lock (_downloadedHistoryLoadGate)
        {
            historyLoadTask = _downloadedHistoryLoadTask;
        }

        if (historyLoadTask != null)
        {
            await historyLoadTask.ConfigureAwait(false);
        }

        Dispose();
        GC.SuppressFinalize(this);
    }

    private static DownloadTaskId GetTaskId(DownloadingItem item)
    {
        return new DownloadTaskId(item.DownloadBase.Id);
    }

    private sealed record PendingDeletion(
        DownloadingItem Item,
        DownloadTaskId TaskId,
        Task Cancellation);
}
