namespace DownKyi.Services.Download;

internal readonly record struct DownloadAddResult(
    DownloadAddOutcome Outcome,
    int AddedCount,
    int DuplicateCount,
    int FailedCount,
    int SkippedCount)
{
    public static DownloadAddResult FromPlan(FinalizedDownload plan)
    {
        var outcome = plan.Outcome switch
        {
            DownloadActionPlanOutcome.NoContentRequested => DownloadAddOutcome.NoContentRequested,
            DownloadActionPlanOutcome.NoPagesSelected => DownloadAddOutcome.NoPagesSelected,
            DownloadActionPlanOutcome.NoAvailableContent => DownloadAddOutcome.NoAvailableContent,
            DownloadActionPlanOutcome.SkippedByUser => DownloadAddOutcome.SkippedByUser,
            _ => DownloadAddOutcome.Failed
        };
        return new DownloadAddResult(
            outcome,
            AddedCount: 0,
            DuplicateCount: 0,
            FailedCount: 0,
            plan.SkippedCount);
    }
}
