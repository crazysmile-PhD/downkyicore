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
using DownKyi.Domain.Results;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

/// <summary>
/// Projects persisted Domain aggregates into desktop list items.
/// Runtime state changes enter through <see cref="IDownloadTaskApplicationService"/>.
/// </summary>
internal sealed class DownloadTaskProjectionStore : IDisposable
{
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadTask> _snapshots = new();
    private readonly ConcurrentDictionary<DownloadTaskId, DownloadingItem> _downloadingProjections = new();
    private bool _disposed;

    public DownloadTaskProjectionStore(IDownloadTaskApplicationService tasks, IClock clock)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tasks.TaskChanged += OnTaskChanged;
    }

    public async Task AddDownloadingAsync(
        DownloadingItem? downloadingItem,
        CancellationToken cancellationToken = default)
    {
        var result = await TryAddDownloadingAsync(
                downloadingItem,
                cancellationToken)
            .ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            ThrowStoreFailure(result.ErrorMessage);
        }
    }

    public async Task<DownloadProjectionAddResult> TryAddDownloadingAsync(
        DownloadingItem? downloadingItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadingItem?.DownloadBase == null)
        {
            return DownloadProjectionAddResult.Success();
        }

        var task =
            DownloadTaskProjectionMapper.CreateNewTask(
                downloadingItem,
                _clock.UtcNow);

        _downloadingProjections[task.Id] =
            downloadingItem;

        var result =
            await _tasks
                .AddAsync(task, cancellationToken)
                .ConfigureAwait(true);

        if (result.IsSuccess)
        {
            return DownloadProjectionAddResult.Success();
        }

        var existing =
            await _tasks
                .FindAsync(task.Id, cancellationToken)
                .ConfigureAwait(true);

        if (existing != null)
        {
            Publish(existing);
            return DownloadProjectionAddResult.Success();
        }

        _downloadingProjections.TryRemove(
            task.Id,
            out _);

        return DownloadProjectionAddResult.Failure(
            result.Error?.Code ==
                "download.store.output_path_reserved",
            result.Error?.Message);
    }
    public async Task<DownloadProjectionAddResult> TryAddDownloadingManyAtomicAsync(
        IReadOnlyList<DownloadingItem> downloadingItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadingItems);

        if (downloadingItems.Count == 0)
        {
            return DownloadProjectionAddResult.Success();
        }

        if (_tasks is not IDownloadTaskAtomicBatchApplicationService batchTasks)
        {
            throw new NotSupportedException(
                "The configured application service does not support atomic batch insertion.");
        }

        var tasks =
            new DownloadTask[downloadingItems.Count];

        for (var index = 0;
             index < downloadingItems.Count;
             index++)
        {
            var item =
                downloadingItems[index];

            ArgumentNullException.ThrowIfNull(item);

            if (item.DownloadBase == null)
            {
                throw new ArgumentException(
                    "A downloading item has no DownloadBase.",
                    nameof(downloadingItems));
            }

            var task =
                DownloadTaskProjectionMapper.CreateNewTask(
                    item,
                    _clock.UtcNow);

            tasks[index] = task;
            _downloadingProjections[task.Id] = item;
        }

        OperationResult result;

        try
        {
            result =
                await batchTasks
                    .AddManyAtomicAsync(
                        tasks,
                        cancellationToken)
                    .ConfigureAwait(true);
        }
        catch
        {
            foreach (var task in tasks)
            {
                _downloadingProjections.TryRemove(
                    task.Id,
                    out _);
            }

            throw;
        }

        if (result.IsSuccess)
        {
            return DownloadProjectionAddResult.Success();
        }

        foreach (var task in tasks)
        {
            _downloadingProjections.TryRemove(
                task.Id,
                out _);
        }

        return DownloadProjectionAddResult.Failure(
            result.Error?.Code ==
                "download.store.output_path_reserved",
            result.Error?.Message);
    }
    public async Task<IReadOnlyList<DownloadingItem>> GetDownloadingAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await GetDownloadingStateAsync(cancellationToken).ConfigureAwait(true);
        return state.Projections;
    }

    public async Task<DownloadTaskProjectionStartupState> GetDownloadingStateAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = await _tasks.GetUnfinishedAsync(cancellationToken).ConfigureAwait(true);
        return new DownloadTaskProjectionStartupState(
            tasks,
            tasks.Select(CreateDownloadingProjection).ToArray());
    }

    public async Task AddMigratedCompletedAsync(
        DownloadTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase != DownloadPhase.Completed)
        {
            throw new ArgumentException("A migrated history task must be completed.", nameof(task));
        }

        var result = await _tasks.AddAsync(task, cancellationToken).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            return;
        }

        var existing = await _tasks.FindAsync(task.Id, cancellationToken).ConfigureAwait(true);
        if (existing?.Phase == DownloadPhase.Completed)
        {
            Publish(existing);
            return;
        }

        ThrowStoreFailure(result.Error?.Message);
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem? downloadedItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        var result = await _tasks
            .DeleteAsync(new DownloadTaskId(downloadedItem.DownloadBase.Id), cancellationToken)
            .ConfigureAwait(true);
        RequireSuccess(result.IsSuccess, result.Error?.Message);
    }

    public async Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _tasks
            .GetHistoryPageAsync(cursor, pageSize, cancellationToken)
            .ConfigureAwait(true);
        foreach (var task in page.Items)
        {
            _snapshots[task.Id] = task;
        }

        return page;
    }

    public async Task<IReadOnlyList<DownloadedItem>> GetDownloadedAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<DownloadedItem>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await GetDownloadedPageAsync(cursor, 500, cancellationToken).ConfigureAwait(true);
            items.AddRange(page.Items.Select(DownloadTaskProjectionMapper.ToDownloadedItem));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return items;
    }

    public async Task<IReadOnlyList<DownloadedItem>> GetRecentDownloadedAsync(
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await GetDownloadedPageAsync(null, pageSize, cancellationToken).ConfigureAwait(true);
        return page.Items.Select(DownloadTaskProjectionMapper.ToDownloadedItem).ToArray();
    }

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        var result = await _tasks.ClearHistoryAsync(cancellationToken).ConfigureAwait(true);
        RequireSuccess(result.IsSuccess, result.Error?.Message);
    }

    public DownloadTask GetRequiredSnapshot(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _snapshots.TryGetValue(taskId, out var task)
            ? task
            : throw new InvalidOperationException($"Download task '{taskId.Value}' is not loaded.");
    }

    public DownloadingItem GetRequiredDownloadingProjection(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _downloadingProjections.TryGetValue(taskId, out var item)
            ? item
            : throw new InvalidOperationException($"Download task '{taskId.Value}' has no active projection.");
    }

    public void PublishLiveProgress(DownloadTaskId taskId, DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(progress);
        if (_downloadingProjections.TryGetValue(taskId, out var item))
        {
            DownloadTaskProjectionMapper.ApplyLiveProgress(progress, item);
        }
    }

    public static DownloadedItem CreateDownloadedProjection(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return DownloadTaskProjectionMapper.ToDownloadedItem(task);
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
        if (args.Kind == DownloadTaskChangeKind.HistoryCleared)
        {
            foreach (var completed in _snapshots
                         .Where(entry => entry.Value.Phase == DownloadPhase.Completed)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _snapshots.TryRemove(completed, out _);
            }

            return;
        }

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
        _snapshots[task.Id] = task;
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

internal sealed record DownloadProjectionAddResult(
    bool IsSuccess,
    bool IsOutputPathConflict,
    string? ErrorMessage)
{
    public static DownloadProjectionAddResult Success() =>
        new(
            IsSuccess: true,
            IsOutputPathConflict: false,
            ErrorMessage: null);

    public static DownloadProjectionAddResult Failure(
        bool isOutputPathConflict,
        string? errorMessage) =>
        new(
            IsSuccess: false,
            IsOutputPathConflict: isOutputPathConflict,
            ErrorMessage: errorMessage);
}
internal sealed record DownloadTaskProjectionStartupState(
    IReadOnlyList<DownloadTask> Tasks,
    IReadOnlyList<DownloadingItem> Projections);
