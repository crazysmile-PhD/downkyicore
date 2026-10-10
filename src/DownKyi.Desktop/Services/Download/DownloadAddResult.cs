namespace DownKyi.Services.Download;

internal readonly record struct DownloadAddResult(
    int AddedCount,
    int DuplicateCount,
    int FailedCount,
    int SkippedCount,
    DownloadPlanningStopReason? StopReason = null)
{
    public bool IsAllDuplicate => AddedCount == 0 && DuplicateCount > 0 && FailedCount == 0;

    public static DownloadAddResult FromPlan(FinalizedDownload plan)
    {
        if (plan.StopReason is not { } stopReason)
        {
            throw new InvalidOperationException("A stopped plan requires a stop reason.");
        }

        return new DownloadAddResult(
            AddedCount: 0,
            DuplicateCount: 0,
            FailedCount: 0,
            plan.SkippedCount,
            stopReason);
    }
}
