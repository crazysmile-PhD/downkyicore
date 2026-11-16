using System;

namespace DownKyi.Services.Download;

// A worker acknowledgement of a durable pause request, not an unrelated timeout.
internal sealed class DownloadPausedException : OperationCanceledException
{
    public DownloadPausedException() : base("Download was paused.") { }
    public DownloadPausedException(string message) : base(message) { }
    public DownloadPausedException(string message, Exception innerException) : base(message, innerException) { }
}
