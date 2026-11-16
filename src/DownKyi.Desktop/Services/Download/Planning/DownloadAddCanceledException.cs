using System.Threading;

namespace DownKyi.Services.Download;

internal sealed class DownloadAddCanceledException : OperationCanceledException
{
    public DownloadAddCanceledException()
    {
    }

    public DownloadAddCanceledException(string message)
        : base(message)
    {
    }

    public DownloadAddCanceledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DownloadAddCanceledException(
        DownloadAddResult partialResult,
        OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base("Adding download tasks was canceled after work had been recorded.", innerException, cancellationToken)
    {
        PartialResult = partialResult;
    }

    public DownloadAddResult PartialResult { get; } = DownloadAddResult.Stopped(
        DownloadAddOutcome.Canceled,
        DownloadAddStopReason.CancellationRequested);
}
