using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed record DownloadHistoryCursor(
    long FinishedTimestamp,
    DownloadTaskId TaskId,
    string MainTitle = "",
    int Order = 0);

public sealed record DownloadHistoryPage(
    IReadOnlyList<DownloadHistoryRecord> Items,
    DownloadHistoryCursor? NextCursor);
