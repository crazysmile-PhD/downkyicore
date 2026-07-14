using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Domain.Tests;

public sealed class DownloadTaskStateMachineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LegalLifecycleCreatesNewVersionsWithoutMutatingEarlierSnapshots()
    {
        var queued = CreateTask();

        var downloading = queued.Start(Epoch.AddSeconds(1)).RequireValue();
        var progressing = downloading
            .UpdateProgress(new DownloadProgress(50, 50, 100, 25), Epoch.AddSeconds(2))
            .RequireValue();
        var pausing = progressing.Pause(Epoch.AddSeconds(3)).RequireValue();
        var paused = pausing.ConfirmPaused(Epoch.AddSeconds(4)).RequireValue();
        var resumed = paused.Resume(Epoch.AddSeconds(5)).RequireValue();
        var restarted = resumed.Start(Epoch.AddSeconds(6)).RequireValue();
        var completed = restarted.Complete(
            new DownloadCompletion(1_789_000_000, "2026-09-08 00:00:00", "25 MB/s"),
            Epoch.AddSeconds(7)).RequireValue();
        var deleted = completed.Delete(Epoch.AddSeconds(8)).RequireValue();

        Assert.Equal(DownloadPhase.Queued, queued.Phase);
        Assert.Equal(DownloadPhase.Downloading, downloading.Phase);
        Assert.Equal(DownloadPhase.Pausing, pausing.Phase);
        Assert.Equal(DownloadPhase.Paused, paused.Phase);
        Assert.Equal(DownloadPhase.Completed, completed.Phase);
        Assert.Equal(DownloadPhase.Deleted, deleted.Phase);
        Assert.Equal(8, deleted.Version);
        Assert.Equal(50, completed.Progress.Percentage);
        Assert.NotNull(completed.Completion);
    }

    [Fact]
    public void FailureRetryCancellationAndDeletionRemainDistinctTransitions()
    {
        var downloading = CreateTask().Start(Epoch.AddSeconds(1)).RequireValue();
        var failure = new DownloadFailure("network.timeout", "Transfer timed out.", true);

        var failed = downloading.Fail(failure, Epoch.AddSeconds(2)).RequireValue();
        var retried = failed.Retry(Epoch.AddSeconds(3)).RequireValue();
        var canceled = retried.Cancel(Epoch.AddSeconds(4)).RequireValue();
        var deleted = canceled.Delete(Epoch.AddSeconds(5)).RequireValue();

        Assert.Same(failure, failed.Failure);
        Assert.Null(retried.Failure);
        Assert.Equal(DownloadPhase.Canceled, canceled.Phase);
        Assert.Equal(DownloadPhase.Deleted, deleted.Phase);
    }

    [Fact]
    public void InvalidTransitionReturnsTypedConflictAndKeepsOriginalSnapshot()
    {
        var queued = CreateTask();

        var result = queued.Complete(
            new DownloadCompletion(1, "finished", null),
            Epoch.AddSeconds(1));

        Assert.False(result.IsSuccess);
        Assert.Equal(OperationErrorKind.Conflict, result.Error?.Kind);
        Assert.Equal("download.transition.invalid", result.Error?.Code);
        Assert.Equal(DownloadPhase.Queued, queued.Phase);
        Assert.Equal(0, queued.Version);
    }

    [Fact]
    public void PlanAndTransferStateDefensivelyCopyCallerCollections()
    {
        var assets = new Dictionary<string, bool> { ["video"] = true };
        var files = new Dictionary<string, string> { ["video"] = "video.m4s" };
        var completed = new List<string> { "cover" };
        var plan = new DownloadPlan(assets, files, streamType: 2);
        var transfer = new DownloadTransferState("aria-gid", completed);

        assets["audio"] = true;
        files["audio"] = "audio.m4s";
        completed.Add("subtitle");

        Assert.Single(plan.RequestedAssets);
        Assert.Single(plan.TransferFiles);
        Assert.Equal("cover", Assert.Single(transfer.CompletedFileKeys));
    }

    [Fact]
    public void RestoreRejectsPhasePayloadThatCouldCorruptStateMeaning()
    {
        var task = CreateTask();

        Assert.Throws<ArgumentException>(() => DownloadTask.Restore(
            task.Id,
            task.Metadata,
            task.Plan,
            task.Output,
            DownloadPhase.Failed,
            task.Progress,
            task.Transfer,
            failure: null,
            completion: null,
            version: 1,
            task.CreatedAtUtc,
            task.UpdatedAtUtc.AddSeconds(1)));
    }

    [Fact]
    public void UpdatesRejectTimestampsOlderThanTheCurrentSnapshot()
    {
        var downloading = CreateTask().Start(Epoch.AddSeconds(2)).RequireValue();

        Assert.Throws<ArgumentOutOfRangeException>(() => downloading.UpdateProgress(
            new DownloadProgress(1),
            Epoch.AddSeconds(1)));
    }

    [Fact]
    public void TerminalTaskRejectsFurtherTransferMutation()
    {
        var completed = CreateTask()
            .Start(Epoch.AddSeconds(1)).RequireValue()
            .Complete(new DownloadCompletion(1, "finished", null), Epoch.AddSeconds(2)).RequireValue();

        var result = completed.UpdateTransferState(
            new DownloadTransferState("late-gid", []),
            Epoch.AddSeconds(3));

        Assert.False(result.IsSuccess);
        Assert.Equal("download.transfer.terminal", result.Error?.Code);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ProgressRejectsInvalidPercentages(double percentage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadProgress(percentage));
    }

    private static DownloadTask CreateTask()
    {
        var metadata = new DownloadTaskMetadata(
            new DownloadMediaIdentity("BV1test", 1, 2, 0, 1, 1),
            "Series",
            "Episode",
            "01:00",
            "AVC",
            new DownloadQuality(80, "1080P"),
            new DownloadQuality(30280, "AAC"),
            "https://example.invalid/cover.jpg",
            "https://example.invalid/page.jpg",
            1);
        var plan = new DownloadPlan(
            new Dictionary<string, bool> { ["video"] = true },
            new Dictionary<string, string> { ["video"] = "video.m4s" },
            streamType: 1);

        return DownloadTask.Create(
            new DownloadTaskId("task-01"),
            metadata,
            plan,
            new DownloadOutput("episode-01", "100 MB"),
            Epoch);
    }
}
