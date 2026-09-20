using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
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

    Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task RemoveDownloadedAsync(DownloadedItem item, CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenVideoAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenFolderAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);
}

internal sealed class DownloadManagerCoordinator : IDownloadManagerCoordinator, IDisposable
{
    private readonly DownloadTaskProjectionStore _storage;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly DownloadTaskFileService _fileService;
    private readonly DownloadListState _downloadLists;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly SemaphoreSlim _pauseResumeItemGate = new(1, 1);
    private readonly object _pauseResumeLifecycleGate = new();
    private TaskCompletionSource? _activePauseResumeBatch;
    private int _pauseResumeBatchVersion;
    private int _activePauseResumeBatches;
    private int _disposed;

    public DownloadManagerCoordinator(
        DownloadTaskProjectionStore storage,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskQueue taskQueue,
        IDownloadRuntimeAvailability runtimeAvailability,
        DownloadTaskFileService fileService,
        DownloadListState downloadLists,
        IPlatformLauncher platformLauncher)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _runtimeAvailability = runtimeAvailability
            ?? throw new ArgumentNullException(nameof(runtimeAvailability));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
    }

    public Task PauseAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        return RunLatestPauseResumeBatchAsync(
            items,
            PauseBatchItemAsync,
            cancellationToken);
    }

    public Task ResumeAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        return RunLatestPauseResumeBatchAsync(
            items,
            ResumeBatchItemAsync,
            cancellationToken);
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
        await _fileService.CancelActiveDownloadAsync(item).ConfigureAwait(true);

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
        foreach (var item in items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(item, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        await _storage.ClearDownloadedAsync(cancellationToken).ConfigureAwait(true);
        _downloadLists.ClearDownloaded();
    }

    public Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        _storage.GetDownloadedPageAsync(cursor, pageSize, cancellationToken);

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
        try
        {
            await _taskQueue.EnqueueAsync(resumed.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (DownloadRuntimeUnavailableException)
        {
            await _stateWriter.FailRuntimeUnavailableAsync(
                resumed.Id,
                CancellationToken.None).ConfigureAwait(true);
            throw;
        }
    }

    private async Task ResumeBatchItemAsync(
        DownloadingItem item,
        Task superseded,
        CancellationToken cancellationToken)
    {
        var taskId = GetTaskId(item);
        if (!_storage.TryGetSnapshot(taskId, out var snapshot)
            || snapshot.Phase is not (
            DownloadPhase.Queued
            or DownloadPhase.Pausing
            or DownloadPhase.Paused
            or DownloadPhase.Failed))
        {
            return;
        }

        _runtimeAvailability.EnsureAcceptingTasks();
        if (snapshot.Phase == DownloadPhase.Pausing)
        {
            snapshot = await _storage
                .WaitForSnapshotPhaseChangeAsync(
                    taskId,
                    DownloadPhase.Pausing,
                    superseded,
                    cancellationToken)
                .ConfigureAwait(true);
            if (snapshot == null || superseded.IsCompleted)
            {
                return;
            }
        }

        if (snapshot.Phase is not (
            DownloadPhase.Queued or DownloadPhase.Paused or DownloadPhase.Failed))
        {
            return;
        }

        var resumed = await _stateWriter
            .ResumeAsync(taskId, cancellationToken)
            .ConfigureAwait(true);
        if (resumed.Phase != DownloadPhase.Queued)
        {
            return;
        }

        try
        {
            await _taskQueue.EnqueueAsync(resumed.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (DownloadRuntimeUnavailableException)
        {
            await _stateWriter.FailRuntimeUnavailableAsync(
                resumed.Id,
                CancellationToken.None).ConfigureAwait(true);
            throw;
        }
    }

    private async Task PauseBatchItemAsync(
        DownloadingItem item,
        Task superseded,
        CancellationToken cancellationToken)
    {
        _ = superseded;
        var taskId = GetTaskId(item);
        if (!_storage.TryGetSnapshot(taskId, out var snapshot)
            || snapshot.Phase is not (
            DownloadPhase.Queued or DownloadPhase.Downloading))
        {
            return;
        }

        await _stateWriter.PauseAsync(taskId, cancellationToken).ConfigureAwait(true);
    }

    private async Task RunLatestPauseResumeBatchAsync(
        IEnumerable<DownloadingItem> items,
        Func<DownloadingItem, Task, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(applyAsync);
        var (batchVersion, superseded) = BeginPauseResumeBatch();
        try
        {
            foreach (var item in items.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batchVersion != Volatile.Read(ref _pauseResumeBatchVersion))
                {
                    return;
                }

                await _pauseResumeItemGate.WaitAsync(cancellationToken).ConfigureAwait(true);
                try
                {
                    if (batchVersion != Volatile.Read(ref _pauseResumeBatchVersion))
                    {
                        return;
                    }

                    await applyAsync(item, superseded, cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    _pauseResumeItemGate.Release();
                }
            }
        }
        finally
        {
            EndPauseResumeBatch();
        }
    }

    public void Dispose()
    {
        var disposeItemGate = false;
        lock (_pauseResumeLifecycleGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            Interlocked.Increment(ref _pauseResumeBatchVersion);
            _activePauseResumeBatch?.TrySetResult();
            disposeItemGate = _activePauseResumeBatches == 0;
        }

        if (disposeItemGate)
        {
            _pauseResumeItemGate.Dispose();
        }
    }

    private (int Version, Task Superseded) BeginPauseResumeBatch()
    {
        lock (_pauseResumeLifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _activePauseResumeBatch?.TrySetResult();
            _activePauseResumeBatch = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activePauseResumeBatches++;
            return (
                Interlocked.Increment(ref _pauseResumeBatchVersion),
                _activePauseResumeBatch.Task);
        }
    }

    private void EndPauseResumeBatch()
    {
        var disposeItemGate = false;
        lock (_pauseResumeLifecycleGate)
        {
            _activePauseResumeBatches--;
            disposeItemGate = _disposed != 0 && _activePauseResumeBatches == 0;
        }

        if (disposeItemGate)
        {
            _pauseResumeItemGate.Dispose();
        }
    }

    private static DownloadTaskId GetTaskId(DownloadingItem item)
    {
        return new DownloadTaskId(item.DownloadBase.Id);
    }
}
