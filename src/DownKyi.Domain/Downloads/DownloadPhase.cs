namespace DownKyi.Domain.Downloads;

public enum DownloadPhase
{
    Queued,
    Downloading,
    Pausing,
    Paused,
    Completed,
    Failed,
    Canceled,
    Deleted
}
