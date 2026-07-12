using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed record DownloadProgressWrite
{
    public DownloadProgressWrite(
        DownloadTaskId taskId,
        DownloadProgress progress,
        long expectedVersion,
        long targetVersion,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetVersion, expectedVersion);

        TaskId = taskId;
        Progress = progress;
        ExpectedVersion = expectedVersion;
        TargetVersion = targetVersion;
        UpdatedAtUtc = updatedAtUtc;
    }

    public DownloadTaskId TaskId { get; }

    public DownloadProgress Progress { get; }

    public long ExpectedVersion { get; private init; }

    public long TargetVersion { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public DownloadProgressWrite Merge(DownloadProgressWrite newer)
    {
        ArgumentNullException.ThrowIfNull(newer);
        if (TaskId != newer.TaskId || TargetVersion != newer.ExpectedVersion)
        {
            throw new InvalidOperationException("Progress writes must be contiguous updates for the same task.");
        }

        return newer with { ExpectedVersion = ExpectedVersion };
    }
}
