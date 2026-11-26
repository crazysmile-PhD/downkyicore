using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

/// <summary>
/// Projects persisted Domain aggregates into desktop list items.
/// Runtime state changes enter through <see cref="IDownloadTaskStateService"/>.
/// </summary>
internal sealed class DownloadTaskProjectionStore : IDisposable
{
    private readonly IDownloadTaskCatalog _tasks;
    private readonly IDownloadHistoryService _history;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadTask> _snapshots = new();
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadingItem> _downloadingProjections = new();
    private bool _disposed;

    public DownloadTaskProjectionStore(
        IDownloadTaskCatalog tasks,
        IDownloadHistoryService history,
        IClock clock)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tasks.TaskChanged += OnTaskChanged;
    }

    public async Task AddDownloadingAsync(
        DownloadingItem? downloadingItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadingItem?.DownloadBase == null)
        {
            return;
        }

        var task = DownloadTaskProjectionMapper.CreateNewTask(downloadingItem, _clock.UtcNow);
        _downloadingProjections[task.Id] = downloadingItem;
        var result = await _tasks.AddAsync(task, cancellationToken).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            return;
        }

        var existing = await _tasks.FindAsync(task.Id, cancellationToken).ConfigureAwait(true);
        if (existing != null)
        {
            Publish(existing);
            return;
        }

        _downloadingProjections.TryRemove(task.Id, out _);
        ThrowStoreFailure(result.Error?.Message);
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

    public async Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _history
            .GetPageAsync(cursor, pageSize, cancellationToken)
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
        if (_snapshots.TryGetValue(taskId, out var snapshot) &&
            snapshot.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing &&
            _downloadingProjections.TryGetValue(taskId, out var item))
        {
            DownloadTaskProjectionMapper.ApplyLiveProgress(progress, item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tasks.TaskChanged -= OnTaskChanged;
        _downloadingProjections.Clear();
        _snapshots.Clear();
        _disposed = true;
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
        if (args.Kind == DownloadTaskChangeKind.Deleted)
        {
            _snapshots.TryRemove(args.TaskId, out _);
            _downloadingProjections.TryRemove(args.TaskId, out _);
            return;
        }

        if (args.Snapshot != null)
        {
            Publish(args.Snapshot);
        }
    }

    private void Publish(DownloadTask task)
    {
        if (task.Phase == DownloadPhase.Completed)
        {
            _snapshots.TryRemove(task.Id, out _);
        }
        else
        {
            _snapshots[task.Id] = task;
        }
        if (_downloadingProjections.TryGetValue(task.Id, out var item))
        {
            DownloadTaskProjectionMapper.Apply(task, item);
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
}

internal sealed record DownloadTaskProjectionStartupState(
    IReadOnlyList<DownloadTask> Tasks,
    IReadOnlyList<DownloadingItem> Projections);
