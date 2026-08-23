using System.Collections.Concurrent;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public sealed class DownloadTaskApplicationService : IDownloadTaskApplicationService, IDisposable
{
    private const int MaximumUpdateAttempts = 2;
    private readonly IDownloadTaskStore _store;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<DownloadTaskId, SemaphoreSlim> _taskGates = new();
    private bool _disposed;

    public DownloadTaskApplicationService(IDownloadTaskStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public event EventHandler<DownloadTaskChangedEventArgs>? TaskChanged;

    public async Task<OperationResult<DownloadTask>> AddAsync(
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _store.AddAsync(task, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return OperationResult.Failure<DownloadTask>(RequireError(result));
        }

        Publish(task, DownloadTaskChangeKind.Added);
        return OperationResult.Success(task);
    }

    public Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.FindAsync(taskId, cancellationToken);
    }

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.GetUnfinishedAsync(cancellationToken);
    }

    public Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken);
    }

    public Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.GetHistoryPageAsync(cursor, pageSize, cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> StartAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Start(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> PauseAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Pause(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> ConfirmPausedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.ConfirmPaused(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> ResumeAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Resume(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> RetryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Retry(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> RecoverInterruptedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.RecoverInterrupted(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> FailAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateAsync(taskId, (task, now) => task.Fail(failure, now), cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> CompleteAsync(
        DownloadTaskId taskId,
        DownloadCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return MutateAsync(taskId, (task, now) => task.Complete(completion, now), cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> RecordTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return MutateAsync(taskId, (task, now) =>
        {
            var files = task.Plan.TransferFiles.SetItem(key, filePath);
            var plan = new DownloadPlan(task.Plan.RequestedAssets, files, task.Plan.StreamType);
            var transfer = CopyTransfer(task.Transfer, backendIdentity: null, replaceBackendIdentity: true);
            return task.UpdatePlan(plan, transfer, now);
        }, cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> ClaimTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return MutateAsync(taskId, (task, now) =>
        {
            var files = task.Plan.TransferFiles;
            if (files.Values.Contains(filePath, StringComparer.Ordinal))
            {
                return task.UpdatePlan(task.Plan, task.Transfer, now);
            }

            var claimKey = key;
            if (files.ContainsKey(claimKey))
            {
                for (var suffix = 1; suffix <= files.Count + 1; suffix++)
                {
                    var candidate = $"{key}-owner-{suffix:D4}";
                    if (!files.ContainsKey(candidate))
                    {
                        claimKey = candidate;
                        break;
                    }
                }
            }

            var claimedFiles = files.Add(claimKey, filePath);
            var plan = new DownloadPlan(
                task.Plan.RequestedAssets,
                claimedFiles,
                task.Plan.StreamType);
            return task.UpdatePlan(plan, task.Transfer, now);
        }, cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> InvalidateCompletedFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return InvalidateCompletedFilesAsync(taskId, [key], cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> InvalidateCompletedFilesAsync(
        DownloadTaskId taskId,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one completed file key is required.", nameof(keys));
        }

        var distinctKeys = keys
            .Select(key =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                return key;
            })
            .ToHashSet(StringComparer.Ordinal);
        return MutateAsync(taskId, (task, now) => task.UpdateTransferState(
            CopyTransfer(
                task.Transfer,
                backendIdentity: null,
                replaceBackendIdentity: true,
                completedFileKeys: task.Transfer.CompletedFileKeys
                    .Where(key => !distinctKeys.Contains(key))),
            now), cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> CompleteTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return MutateAsync(taskId, (task, now) =>
        {
            var completed = task.Transfer.CompletedFileKeys.Contains(key, StringComparer.Ordinal)
                ? task.Transfer.CompletedFileKeys
                : task.Transfer.CompletedFileKeys.Add(key);
            return task.UpdateTransferState(
                CopyTransfer(
                    task.Transfer,
                    backendIdentity: null,
                    replaceBackendIdentity: true,
                    completedFileKeys: completed),
                now);
        }, cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> SetBackendIdentityAsync(
        DownloadTaskId taskId,
        string? backendIdentity,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, (task, now) => task.UpdateTransferState(
            CopyTransfer(task.Transfer, backendIdentity, replaceBackendIdentity: true),
            now), cancellationToken);

    public Task<OperationResult<DownloadTask>> UpdateActivityAsync(
        DownloadTaskId taskId,
        string? activeContent,
        string? statusText,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, (task, now) => task.UpdateTransferState(
            new DownloadTransferState(
                task.Transfer.BackendIdentity,
                task.Transfer.CompletedFileKeys,
                activeContent,
                statusText,
                task.Transfer.MaximumBytesPerSecond),
            now), cancellationToken);

    public Task<OperationResult<DownloadTask>> UpdateProgressAsync(
        DownloadTaskId taskId,
        DownloadProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return MutateAsync(taskId, (task, now) => task.UpdateProgressAndTransfer(
            progress,
            CopyTransfer(
                task.Transfer,
                maximumBytesPerSecond: Math.Max(
                    task.Transfer.MaximumBytesPerSecond,
                    progress.BytesPerSecond)),
            now), cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> UpdateOutputFileSizeAsync(
        DownloadTaskId taskId,
        string? fileSizeText,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, (task, now) => task.UpdateOutput(
            new DownloadOutput(task.Output.BasePath, fileSizeText),
            now), cancellationToken);

    public Task<OperationResult<DownloadTask>> CancelAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Cancel(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Delete(now), cancellationToken);

    public async Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _store.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            TaskChanged?.Invoke(this, new DownloadTaskChangedEventArgs(
                new DownloadTaskId("history"),
                null,
                DownloadTaskChangeKind.HistoryCleared));
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var gate in _taskGates.Values)
        {
            gate.Dispose();
        }

        _taskGates.Clear();
        _disposed = true;
    }

    private async Task<OperationResult<DownloadTask>> MutateAsync(
        DownloadTaskId taskId,
        Func<DownloadTask, DateTimeOffset, OperationResult<DownloadTask>> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(transition);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaximumUpdateAttempts; attempt++)
            {
                var current = await _store.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (current == null)
                {
                    return OperationResult.Failure<DownloadTask>(new OperationError(
                        "download.store.not_found",
                        $"Download task '{taskId.Value}' was not found.",
                        OperationErrorKind.NotFound));
                }

                var now = LaterOf(_clock.UtcNow, current.UpdatedAtUtc);
                var transitionResult = transition(current, now);
                if (!transitionResult.TryGetValue(out var updated))
                {
                    return transitionResult;
                }

                var storeResult = await _store
                    .UpdateAsync(updated, current.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (storeResult.IsSuccess)
                {
                    Publish(
                        updated,
                        updated.Phase == DownloadPhase.Deleted
                            ? DownloadTaskChangeKind.Deleted
                            : DownloadTaskChangeKind.Updated);
                    return OperationResult.Success(updated);
                }

                if (storeResult.Error?.Code != "download.store.conflict")
                {
                    return OperationResult.Failure<DownloadTask>(RequireError(storeResult));
                }
            }

            return OperationResult.Failure<DownloadTask>(new OperationError(
                "download.store.conflict",
                $"Download task '{taskId.Value}' changed repeatedly while applying a command.",
                OperationErrorKind.Conflict));
        }
        finally
        {
            gate.Release();
        }
    }

    private void Publish(DownloadTask task, DownloadTaskChangeKind kind)
    {
        TaskChanged?.Invoke(this, new DownloadTaskChangedEventArgs(task.Id, task, kind));
    }

    private static DownloadTransferState CopyTransfer(
        DownloadTransferState source,
        string? backendIdentity = null,
        bool replaceBackendIdentity = false,
        IEnumerable<string>? completedFileKeys = null,
        long? maximumBytesPerSecond = null)
    {
        return new DownloadTransferState(
            replaceBackendIdentity ? backendIdentity : source.BackendIdentity,
            completedFileKeys ?? source.CompletedFileKeys,
            source.ActiveContent,
            source.StatusText,
            maximumBytesPerSecond ?? source.MaximumBytesPerSecond);
    }

    private static OperationError RequireError(OperationResult result)
    {
        return result.Error ?? new OperationError(
            "download.store.unknown",
            "The download store rejected an operation without an error.");
    }

    private static DateTimeOffset LaterOf(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }
}
