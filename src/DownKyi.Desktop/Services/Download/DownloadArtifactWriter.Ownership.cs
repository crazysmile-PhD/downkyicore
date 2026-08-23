using System;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    internal const string MainCoverTransferKey = "cover";
    internal const string PageCoverTransferKey = "page-cover";
    internal const string DefaultSubtitleTransferKey = "subtitle";

    internal static string GetSubtitleTrackTransferKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return $"subtitle-{index + 1:D4}";
    }
}
