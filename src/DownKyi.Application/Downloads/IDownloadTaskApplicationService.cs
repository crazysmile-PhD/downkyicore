using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public interface IDownloadTaskApplicationService :
    IDownloadTaskCatalog,
    IDownloadTaskStateService,
    IDownloadOutputReservationService
{
}

public enum DownloadTaskChangeKind
{
    Added,
    Updated,
    Deleted
}

public sealed class DownloadTaskChangedEventArgs : EventArgs
{
    public DownloadTaskChangedEventArgs(
        DownloadTaskId taskId,
        DownloadTask? snapshot,
        DownloadTaskChangeKind kind,
        DownloadTask? previousSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        TaskId = taskId;
        Snapshot = snapshot;
        PreviousSnapshot = previousSnapshot;
        Kind = kind;
    }

    public DownloadTaskId TaskId { get; }

    public DownloadTask? Snapshot { get; }

    public DownloadTask? PreviousSnapshot { get; }

    public DownloadTaskChangeKind Kind { get; }
}
