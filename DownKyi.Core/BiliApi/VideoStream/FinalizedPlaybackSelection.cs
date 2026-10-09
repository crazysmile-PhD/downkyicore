using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

public sealed record FinalizedPlaybackSelection(
    int VideoQuality,
    int? VideoCodecId,
    int? AudioId,
    PlayUrlStreamKind? StreamKind,
    bool RequireVideo)
{
    public int ProbeQuality => VideoQuality > 0
        ? VideoQuality
        : PlaybackQualityCatalog.MaximumProbeQuality;

    public bool IsActionable => RequireVideo
        ? VideoQuality > 0 && VideoCodecId is > 0 && StreamKind != null
        : AudioId is > 0;
}
