namespace DownKyi.Services.Download;

internal enum DownloadAddOutcome
{
    Added,
    NoContentRequested,
    NoPagesSelected,
    NoAvailableContent,
    SkippedByUser,
    AllDuplicate,
    Failed
}
