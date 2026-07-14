using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed record DownloadHistoryCursor(long FinishedTimestamp, DownloadTaskId TaskId);

public sealed record DownloadHistoryPage(
    IReadOnlyList<DownloadTask> Items,
    DownloadHistoryCursor? NextCursor);
