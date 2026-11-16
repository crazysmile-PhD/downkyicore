namespace DownKyi.Services.Download;

internal readonly record struct DownloadAddResult
{
    private DownloadAddResult(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount,
        DownloadAddOutcome outcome,
        DownloadAddStopReason? stopReason)
    {
        AddedCount = addedCount;
        DuplicateCount = duplicateCount;
        FailedCount = failedCount;
        SkippedCount = skippedCount;
        Outcome = outcome;
        StopReason = stopReason;
    }

    public int AddedCount { get; }

    public int DuplicateCount { get; }

    public int FailedCount { get; }

    public int SkippedCount { get; }

    public DownloadAddOutcome Outcome { get; }

    public DownloadAddStopReason? StopReason { get; }

    public bool IsAllDuplicate => AddedCount == 0 && DuplicateCount > 0 && FailedCount == 0;

    public bool HasRecordedWork => AddedCount > 0 || DuplicateCount > 0
        || FailedCount > 0 || SkippedCount > 0;

    public static DownloadAddResult FromCounts(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount,
        DownloadAddStopReason? failureReason = null)
    {
        var hasAcceptedWork = addedCount > 0 || duplicateCount > 0;
        var hasIncompleteWork = failedCount > 0 || skippedCount > 0;
        var outcome = hasAcceptedWork && hasIncompleteWork
            ? DownloadAddOutcome.Partial
            : failedCount > 0
                ? DownloadAddOutcome.Failed
                : DownloadAddOutcome.Completed;
        return new DownloadAddResult(
            addedCount,
            duplicateCount,
            failedCount,
            skippedCount,
            outcome,
            failureReason);
    }

    public static DownloadAddResult Stopped(
        DownloadAddOutcome outcome,
        DownloadAddStopReason stopReason,
        int skippedCount = 0)
    {
        if (outcome is DownloadAddOutcome.Completed or DownloadAddOutcome.Partial)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }

        return new DownloadAddResult(0, 0, 0, skippedCount, outcome, stopReason);
    }

    public static DownloadAddResult CanceledWithProgress(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount) =>
        new(
            addedCount,
            duplicateCount,
            failedCount,
            skippedCount,
            DownloadAddOutcome.Canceled,
            DownloadAddStopReason.CancellationRequested);

    public static DownloadAddResult FromPlan(FinalizedDownload plan)
    {
        if (plan.StopReason is not { } stopReason)
        {
            throw new InvalidOperationException("A stopped plan requires a stop reason.");
        }

        var mappedReason = stopReason switch
        {
            DownloadPlanningStopReason.NoContentRequested => DownloadAddStopReason.NoContentRequested,
            DownloadPlanningStopReason.NoPagesSelected => DownloadAddStopReason.NoPagesSelected,
            DownloadPlanningStopReason.NoAvailableContent => DownloadAddStopReason.NoAvailableContent,
            DownloadPlanningStopReason.SkippedByUser => DownloadAddStopReason.SkippedByUser,
            _ => throw new ArgumentOutOfRangeException(nameof(plan), stopReason, null)
        };
        return Stopped(
            DownloadAddOutcome.Rejected,
            mappedReason,
            plan.SkippedCount);
    }
}
