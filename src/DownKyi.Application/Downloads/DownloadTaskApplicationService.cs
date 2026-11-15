using System.Collections.Concurrent;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public sealed partial class DownloadTaskApplicationService : IDownloadTaskApplicationService, IDisposable
{
    private const int MaximumUpdateAttempts = 2;
    private readonly IDownloadTaskStore _store;
    private readonly IDownloadHistoryService _history;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<DownloadTaskId, SemaphoreSlim> _taskGates = new();
    private bool _disposed;

    public DownloadTaskApplicationService(
        IDownloadTaskStore store,
        IDownloadHistoryService history,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _history = history;
        _clock = clock;
    }

    public event EventHandler<DownloadTaskChangedEventArgs>? TaskChanged;

    public async Task<OperationResult<DownloadTask>> AddAsync(
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (task.Phase is DownloadPhase.Completed or DownloadPhase.Deleted)
        {
            return OperationResult.Failure<DownloadTask>(new OperationError(
                "download.task.not_recoverable",
                "Completed or deleted downloads cannot be added as recoverable tasks.",
                OperationErrorKind.Validation));
        }

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

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.GetUnfinishedByCidAsync(cid, cancellationToken);
    }

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.GetUnfinishedByCidsAsync(cids, cancellationToken);
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

    public Task<OperationResult<DownloadTask>> ReconcileInterruptedAsync(
        DownloadTaskId taskId,
        long snapshotVersion,
        CancellationToken cancellationToken) =>
        MutateAsync(
            taskId,
            static task => task.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing,
            (task, now) => task.Phase == DownloadPhase.Pausing && task.Version != snapshotVersion
                ? task.ConfirmPaused(now)
                : task.RecoverInterrupted(now),
            cancellationToken);

    public Task<OperationResult<DownloadTask>> FailAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateAsync(taskId, (task, now) => task.Fail(failure, now), cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> FailIfRunnableAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateAsync(
            taskId,
            static task => task.Phase is DownloadPhase.Queued or DownloadPhase.Downloading,
            (task, now) => task.Fail(failure, now),
            cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> FailIfDispatchableAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateAsync(
            taskId,
            static task => task.Phase is DownloadPhase.Queued
                or DownloadPhase.Downloading
                or DownloadPhase.Pausing,
            (task, now) => task.Fail(failure, now),
            cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> ConfirmPausedIfPausingAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(
            taskId,
            static task => task.Phase == DownloadPhase.Pausing,
            static (task, now) => task.ConfirmPaused(now),
            cancellationToken);

    public Task<OperationResult<DownloadTask>> DiscardUnpublishedArtifactsAndFailAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateAsync(
            taskId,
            (task, now) => task.DiscardUnpublishedArtifactsAndFail(failure, now),
            cancellationToken);
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
            var plan = task.Plan.WithTransferFiles(files);
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
        return ClaimTransferFilesAsync(
            taskId,
            [new KeyValuePair<string, string>(key, filePath)],
            cancellationToken);
    }

    public Task<OperationResult<DownloadTask>> ClaimTransferFilesAsync(
        DownloadTaskId taskId,
        IReadOnlyCollection<KeyValuePair<string, string>> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var claim in files)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.Value);
        }

        return MutateAsync(taskId, (task, now) =>
        {
            var claimedFiles = task.Plan.TransferFiles;
            foreach (var claim in files)
            {
                if (claimedFiles.Values.Contains(claim.Value, StringComparer.Ordinal))
                {
                    continue;
                }

                var claimKey = claim.Key;
                if (claimedFiles.ContainsKey(claimKey))
                {
                    for (var suffix = 1; suffix <= claimedFiles.Count + 1; suffix++)
                    {
                        var candidate = $"{claim.Key}-owner-{suffix:D4}";
                        if (!claimedFiles.ContainsKey(candidate))
                        {
                            claimKey = candidate;
                            break;
                        }
                    }
                }

                claimedFiles = claimedFiles.Add(claimKey, claim.Value);
            }

            var plan = task.Plan.WithTransferFiles(claimedFiles);
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

    public Task<OperationResult<DownloadTask>> RecordPublishedArtifactAsync(
        DownloadTaskId taskId,
        DownloadPublishingArtifact publishing,
        string path,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, (task, now) => task.RecordPublishedArtifact(publishing, path, now), cancellationToken);

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
                task.Transfer.MaximumBytesPerSecond,
                task.Transfer.IsLegacyTransfer),
            now), cancellationToken);

    public Task<OperationResult<DownloadTask>> UpdateProgressAsync(
        DownloadTaskId taskId,
        DownloadProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return MutateAsync(taskId,
            static task => task.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing,
            (task, now) => task.UpdateProgressAndTransfer(
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
            new DownloadOutput(task.Output.BasePath, fileSizeText, task.Output.PublishedArtifacts,
                task.Output.StagingToken, task.Output.PublishingArtifact),
            now), cancellationToken);

    public Task<OperationResult<DownloadTask>> CancelAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Cancel(now), cancellationToken);

    public Task<OperationResult<DownloadTask>> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.Delete(now), cancellationToken);

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

    private Task<OperationResult<DownloadTask>> MutateAsync(
        DownloadTaskId taskId,
        Func<DownloadTask, DateTimeOffset, OperationResult<DownloadTask>> transition,
        CancellationToken cancellationToken) =>
        MutateAsync(
            taskId,
            static _ => true,
            transition,
            cancellationToken);

    private async Task<OperationResult<DownloadTask>> MutateAsync(
        DownloadTaskId taskId,
        Func<DownloadTask, bool> shouldTransition,
        Func<DownloadTask, DateTimeOffset, OperationResult<DownloadTask>> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(shouldTransition);
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

                if (!shouldTransition(current))
                {
                    return OperationResult.Success(current);
                }

                var now = LaterOf(_clock.UtcNow, current.UpdatedAtUtc);
                var transitionResult = transition(current, now);
                if (!transitionResult.TryGetValue(out var updated))
                {
                    return transitionResult;
                }

                if (ReferenceEquals(current, updated))
                {
                    return transitionResult;
                }

                var storeResult = updated.Phase == DownloadPhase.Completed
                    ? await _history.CompleteAsync(
                            updated,
                            current.Version,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _store
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
            maximumBytesPerSecond ?? source.MaximumBytesPerSecond,
            source.IsLegacyTransfer);
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
