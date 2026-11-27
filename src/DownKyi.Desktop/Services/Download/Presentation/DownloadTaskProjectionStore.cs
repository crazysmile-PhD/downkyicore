using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Platform;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Services.Download;

/// <summary>
/// Projects persisted Domain aggregates into desktop list items.
/// Runtime state changes enter through <see cref="IDownloadTaskStateService"/>.
/// </summary>
internal sealed class DownloadTaskProjectionStore : IDisposable, IAsyncDisposable
{
    private readonly IDownloadTaskCatalog _tasks;
    private readonly IDownloadHistoryService _history;
    private readonly IUiDispatcher? _uiDispatcher;
    private readonly ILogger<DownloadTaskProjectionStore> _logger;
    private readonly CancellationToken _shutdownToken;
    private readonly Lock _presentationGate = new();
    private Task _presentationUpdates = Task.CompletedTask;
    private readonly Dictionary<DownloadTaskId, PendingLiveProgress> _pendingProgress = new();
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadTask> _snapshots = new();
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadingItem> _downloadingProjections = new();
    private volatile bool _disposed;

    public DownloadTaskProjectionStore(
        IDownloadTaskCatalog tasks,
        IDownloadHistoryService history,
        IUiDispatcher? uiDispatcher = null,
        ILogger<DownloadTaskProjectionStore>? logger = null,
        ApplicationCancellation? applicationCancellation = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _uiDispatcher = uiDispatcher;
        _logger = logger ?? NullLogger<DownloadTaskProjectionStore>.Instance;
        _shutdownToken = applicationCancellation?.ShutdownToken ?? CancellationToken.None;
        _tasks.TaskChanged += OnTaskChanged;
    }

    public DownloadingItem Track(DownloadTask task, DownloadingItem? item = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (item != null && item.DownloadBase.Id != task.Id.Value)
        {
            throw new ArgumentException("The projection must represent the committed task.", nameof(item));
        }
        var projection = _downloadingProjections.GetOrAdd(
            task.Id, _ => item ?? DownloadTaskProjectionMapper.ToDownloadingItem(task));
        Publish(task);
        return projection;
    }

    public async Task<DownloadTaskProjectionStartupState> GetDownloadingStateAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = await _tasks.GetUnfinishedAsync(cancellationToken).ConfigureAwait(true);
        return new DownloadTaskProjectionStartupState(
            tasks,
            tasks.Select(CreateDownloadingProjection).ToArray());
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem? downloadedItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        var result = await _history
            .DeleteAsync(downloadedItem.HistoryRecord.Id, cancellationToken)
            .ConfigureAwait(true);
        RequireSuccess(result.IsSuccess, result.Error?.Message);
    }

    public Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetDownloadedPageAsync(cursor, pageSize, DownloadFinishedSort.DownloadDesc, cancellationToken);

    public async Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        DownloadFinishedSort sort,
        CancellationToken cancellationToken = default)
    {
        var page = await _history
            .GetPageAsync(cursor, pageSize, cancellationToken, sort)
            .ConfigureAwait(false);
        return page;
    }

    public async Task<IReadOnlyList<DownloadedItem>> GetDownloadedAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<DownloadedItem>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await GetDownloadedPageAsync(cursor, 500, cancellationToken)
                .ConfigureAwait(false);
            items.AddRange(page.Items.Select(DownloadTaskProjectionMapper.ToDownloadedItem));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return items;
    }

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        var result = await _history.ClearAsync(cancellationToken).ConfigureAwait(true);
        RequireSuccess(result.IsSuccess, result.Error?.Message);
    }

    public DownloadTask GetRequiredSnapshot(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return TryGetSnapshot(taskId, out var task)
            ? task
            : throw new InvalidOperationException($"Download task '{taskId.Value}' is not loaded.");
    }

    public bool TryGetSnapshot(
        DownloadTaskId taskId,
        [NotNullWhen(true)] out DownloadTask? task)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _snapshots.TryGetValue(taskId, out task);
    }

    public DownloadingItem GetRequiredDownloadingProjection(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _downloadingProjections.TryGetValue(taskId, out var item)
            ? item
            : throw new InvalidOperationException($"Download task '{taskId.Value}' has no active projection.");
    }

    public bool TryRemoveDownloadingProjection(
        DownloadTaskId taskId,
        [NotNullWhen(true)] out DownloadingItem? item)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _downloadingProjections.TryRemove(taskId, out item);
    }

    public void PublishLiveProgress(DownloadTaskId taskId, DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(progress);
        lock (_presentationGate)
        {
            if (_disposed || _shutdownToken.IsCancellationRequested ||
                !_snapshots.TryGetValue(taskId, out var snapshot) ||
                snapshot.Phase is not (DownloadPhase.Downloading or DownloadPhase.Pausing) ||
                !_downloadingProjections.ContainsKey(taskId))
            {
                return;
            }

            if (_pendingProgress.TryGetValue(taskId, out var pending) && pending.Version == snapshot.Version)
            {
                pending.Latest = progress;
                pending.MaximumBytesPerSecond = Math.Max(pending.MaximumBytesPerSecond, progress.BytesPerSecond);
                return;
            }

            pending = new PendingLiveProgress(snapshot.Version, progress);
            _pendingProgress[taskId] = pending;
            QueuePresentationUpdate(() => ApplyPendingProgress(taskId, pending));
        }
    }

    private void ApplyPendingProgress(DownloadTaskId taskId, PendingLiveProgress pending)
    {
        DownloadProgress progress;
        long maximumBytesPerSecond;
        lock (_presentationGate)
        {
            if (!_pendingProgress.TryGetValue(taskId, out var currentPending) ||
                !ReferenceEquals(currentPending, pending))
            {
                return;
            }

            _pendingProgress.Remove(taskId);
            progress = pending.Latest;
            maximumBytesPerSecond = pending.MaximumBytesPerSecond;
        }

        if (_snapshots.TryGetValue(taskId, out var current) && current.Version == pending.Version &&
            current.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing &&
            _downloadingProjections.TryGetValue(taskId, out var item))
        {
            DownloadTaskProjectionMapper.ApplyLiveProgress(progress, item, maximumBytesPerSecond);
        }
    }

    public void UpdatePresentation(DownloadTaskId taskId, Action<DownloadingItem> update)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(update);
        QueuePresentationUpdate(() =>
        {
            if (_downloadingProjections.TryGetValue(taskId, out var item))
            {
                update(item);
            }
        });
    }

    public Task FlushPresentationAsync()
    {
        lock (_presentationGate)
        {
            return _presentationUpdates;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_presentationGate)
        {
            _disposed = true;
            _pendingProgress.Clear();
        }
        _tasks.TaskChanged -= OnTaskChanged;
        _downloadingProjections.Clear();
        _snapshots.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await FlushPresentationAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private DownloadingItem CreateDownloadingProjection(DownloadTask task)
    {
        _snapshots[task.Id] = task;
        return _downloadingProjections.GetOrAdd(
            task.Id,
            _ => DownloadTaskProjectionMapper.ToDownloadingItem(task));
    }

    private void OnTaskChanged(object? sender, DownloadTaskChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }
        if (args.Kind == DownloadTaskChangeKind.Deleted)
        {
            lock (_presentationGate)
            {
                _pendingProgress.Remove(args.TaskId);
                _snapshots.TryRemove(args.TaskId, out _);
                _downloadingProjections.TryRemove(args.TaskId, out _);
            }
            return;
        }

        if (args.Snapshot != null)
        {
            Publish(args.Snapshot);
        }
    }

    private void Publish(DownloadTask task)
    {
        lock (_presentationGate)
        {
            if (_disposed) return;
            _snapshots.TryGetValue(task.Id, out var preceding);
            if (_pendingProgress.TryGetValue(task.Id, out var pending) &&
                preceding != null && pending.Version == preceding.Version && task.Progress == preceding.Progress &&
                task.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing)
            {
                // Activity-only commits do not supersede accepted live progress.
                pending.Version = task.Version;
            }
            else
            {
                _pendingProgress.Remove(task.Id);
            }
            if (task.Phase == DownloadPhase.Completed)
            {
                _snapshots.TryRemove(task.Id, out _);
            }
            else
            {
                _snapshots[task.Id] = task;
            }
            UpdatePresentation(task.Id, item => DownloadTaskProjectionMapper.Apply(task, item, preceding));
        }
    }

    private void QueuePresentationUpdate(Action update)
    {
        lock (_presentationGate)
        {
            if (_disposed)
            {
                return;
            }
            if (_uiDispatcher == null)
            {
                // Unbound compatibility models do not have a desktop dispatcher.
                update();
                return;
            }

            _presentationUpdates = ApplyPresentationAsync(_presentationUpdates, update);
        }
    }

    private async Task ApplyPresentationAsync(Task preceding, Action update)
    {
        try
        {
            await preceding.ConfigureAwait(false);
            if (_disposed || _shutdownToken.IsCancellationRequested) return;
            await _uiDispatcher!.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    update();
                }
            }).WaitAsync(_shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            // Shutdown can finish after the dispatcher stops servicing queued work.
            // The disposed guard also makes any later dispatcher callback inert.
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or KeyNotFoundException or OperationCanceledException)
        {
            // A presentation failure cannot change an already committed command result.
            // This owned chain is drained before asynchronous disposal completes.
            _logger.LogErrorMessage("Download presentation update failed.", exception);
        }
    }

    private static void RequireSuccess(bool isSuccess, string? message)
    {
        if (!isSuccess)
        {
            ThrowStoreFailure(message);
        }
    }

    [DoesNotReturn]
    private static void ThrowStoreFailure(string? message)
    {
        throw new InvalidOperationException(message ?? "Download storage operation failed.");
    }

    private sealed class PendingLiveProgress(long version, DownloadProgress progress)
    {
        public long Version { get; set; } = version;
        public DownloadProgress Latest { get; set; } = progress;
        public long MaximumBytesPerSecond { get; set; } = progress.BytesPerSecond;
    }
}

internal sealed record DownloadTaskProjectionStartupState(
    IReadOnlyList<DownloadTask> Tasks,
    IReadOnlyList<DownloadingItem> Projections);
