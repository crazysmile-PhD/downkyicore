namespace DownKyi.Services.Download;

internal enum DownloadAddStopReason
{
    UnsupportedInput,
    SelectionCanceled,
    SelectionUnavailable,
    NoContentRequested,
    NoPagesSelected,
    NoAvailableContent,
    SkippedByUser,
    RuntimeUnavailable,
    TaskAdmissionFailed,
    CancellationRequested
}
