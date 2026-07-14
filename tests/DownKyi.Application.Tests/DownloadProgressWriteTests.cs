using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Tests;

public sealed class DownloadProgressWriteTests
{
    [Fact]
    public void MergePreservesFirstExpectedVersionAndLatestProgress()
    {
        var id = new DownloadTaskId("task-01");
        var first = new DownloadProgressWrite(
            id,
            new DownloadProgress(10),
            expectedVersion: 3,
            targetVersion: 4,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var second = new DownloadProgressWrite(
            id,
            new DownloadProgress(20),
            expectedVersion: 4,
            targetVersion: 5,
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        var merged = first.Merge(second);

        Assert.Equal(3, merged.ExpectedVersion);
        Assert.Equal(5, merged.TargetVersion);
        Assert.Equal(20, merged.Progress.Percentage);
    }

    [Fact]
    public void MergeRejectsVersionGaps()
    {
        var id = new DownloadTaskId("task-01");
        var first = new DownloadProgressWrite(
            id,
            DownloadProgress.None,
            expectedVersion: 3,
            targetVersion: 4,
            DateTimeOffset.UnixEpoch);
        var noncontiguous = new DownloadProgressWrite(
            id,
            DownloadProgress.None,
            expectedVersion: 5,
            targetVersion: 6,
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => first.Merge(noncontiguous));
    }
}
