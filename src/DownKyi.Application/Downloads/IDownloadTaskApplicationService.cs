using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public interface IDownloadTaskApplicationService
{
    event EventHandler<DownloadTaskChangedEventArgs>? TaskChanged;

    Task<OperationResult<DownloadTask>> AddAsync(
        DownloadTask task,
        CancellationToken cancellationToken);

    Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken);

    Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> StartAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> PauseAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> ConfirmPausedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> ResumeAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> RetryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> RecoverInterruptedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> FailAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> CompleteAsync(
        DownloadTaskId taskId,
        DownloadCompletion completion,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> RecordTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> ClaimTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> InvalidateCompletedFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> InvalidateCompletedFilesAsync(
        DownloadTaskId taskId,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> CompleteTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> SetBackendIdentityAsync(
        DownloadTaskId taskId,
        string? backendIdentity,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> UpdateActivityAsync(
        DownloadTaskId taskId,
        string? activeContent,
        string? statusText,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> UpdateProgressAsync(
        DownloadTaskId taskId,
        DownloadProgress progress,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> UpdateOutputFileSizeAsync(
        DownloadTaskId taskId,
        string? fileSizeText,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> CancelAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult<DownloadTask>> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken);
}

public enum DownloadTaskChangeKind
{
    Added,
    Updated,
    Deleted,
    HistoryCleared
}

public sealed class DownloadTaskChangedEventArgs : EventArgs
{
    public DownloadTaskChangedEventArgs(
        DownloadTaskId taskId,
        DownloadTask? snapshot,
        DownloadTaskChangeKind kind)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        TaskId = taskId;
        Snapshot = snapshot;
        Kind = kind;
    }

    public DownloadTaskId TaskId { get; }

    public DownloadTask? Snapshot { get; }

    public DownloadTaskChangeKind Kind { get; }
}
