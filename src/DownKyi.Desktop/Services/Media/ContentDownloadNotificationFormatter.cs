using DownKyi.Services.Download;

namespace DownKyi.Services.Media;

internal static class ContentDownloadNotificationFormatter
{
    public static string Format(ContentDownloadBatchResult result)
    {
        return DownloadAddNotificationFormatter.FormatCounts(
            result.AddedCount,
            result.DuplicateCount,
            result.FailedCount,
            result.SkippedCount);
    }
}
