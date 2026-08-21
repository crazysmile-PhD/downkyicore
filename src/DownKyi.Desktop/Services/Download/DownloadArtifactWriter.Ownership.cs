using System;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    internal const string MainCoverTransferKey = "cover";
    internal const string PageCoverTransferKey = "page-cover";
    internal const string DefaultSubtitleTransferKey = "subtitle";
    internal const string CoverArtifactKind = "cover";
    internal const string PageCoverArtifactKind = "page-cover";
    internal const string DanmakuArtifactKind = "danmaku";
    internal const string SubtitleArtifactKind = "subtitle";
    internal const string NfoArtifactKind = "nfo";

    internal static string GetSubtitleTrackTransferKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return $"subtitle-{index + 1:D4}";
    }
}
