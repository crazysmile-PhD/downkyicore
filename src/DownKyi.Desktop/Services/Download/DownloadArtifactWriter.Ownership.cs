using System;
using System.Globalization;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    internal const string MainCoverTransferKey = "cover";
    internal const string PageCoverTransferKey = "page-cover";
    internal const string DefaultSubtitleTransferKey = "subtitle";
    internal const string DefaultSubtitleArtifactKey = "subtitle:default";
    internal const string DanmakuAssTransferKey = "danmaku";
    internal const string DanmakuXmlTransferKey = "danmaku-xml";
    private const string SubtitleTrackTransferKeyPrefix = "subtitle-track:";
    private const string SubtitleTrackArtifactKeyPrefix = "subtitle:track:";

    internal static string GetSubtitleTrackTransferKey(long trackId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trackId);
        return SubtitleTrackTransferKeyPrefix + trackId.ToString(CultureInfo.InvariantCulture);
    }

    internal static string GetSubtitleArtifactKey(long trackId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trackId);
        return SubtitleTrackArtifactKeyPrefix + trackId.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryGetSubtitleTrackIdFromTransferKey(string key, out long trackId) =>
        TryGetSubtitleTrackId(key, SubtitleTrackTransferKeyPrefix, allowOwnerSuffix: true, out trackId);

    internal static bool TryGetSubtitleTrackIdFromArtifactKey(string key, out long trackId) =>
        TryGetSubtitleTrackId(key, SubtitleTrackArtifactKeyPrefix, allowOwnerSuffix: false, out trackId);

    private static bool TryGetSubtitleTrackId(
        string key,
        string prefix,
        bool allowOwnerSuffix,
        out long trackId)
    {
        trackId = default;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = key.AsSpan(prefix.Length);
        if (allowOwnerSuffix)
        {
            var suffix = value.IndexOf("-owner-", StringComparison.Ordinal);
            if (suffix >= 0)
            {
                value = value[..suffix];
            }
        }

        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out trackId)
            && trackId >= 0;
    }
}
