using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public enum DownloadMediaKind
{
    None,
    Dash,
    Durl
}

[Flags]
public enum DownloadDanmakuOutputFormat
{
    None = 0,
    Ass = 1,
    Xml = 2
}

public enum DownloadSubtitleTrackSelection
{
    NotRequested,
    AllTracks,
    SelectedTracks,
    NoTracksSelected
}

public sealed record DownloadContentSelection(
    bool Audio,
    bool Video,
    bool Danmaku,
    bool Subtitle,
    bool Cover)
{
    public DownloadMediaKind? MediaKind { get; init; }

    public ImmutableArray<long>? SelectedSubtitleTrackIds { get; init; }

    public long? DefaultSubtitleTrackId { get; init; }

    public DownloadDanmakuOutputFormat? DanmakuOutputFormat { get; init; }

    public bool HasMedia => Audio || Video;

    public bool HasSubtitleAction => SubtitleTrackSelection is
        DownloadSubtitleTrackSelection.AllTracks or
        DownloadSubtitleTrackSelection.SelectedTracks;

    public bool HasIndependentContent => Danmaku || HasSubtitleAction || Cover;

    public bool HasAnyRequestedAction => HasMedia || HasIndependentContent;

    public DownloadSubtitleTrackSelection SubtitleTrackSelection =>
        (Subtitle, SelectedSubtitleTrackIds) switch
        {
            (false, _) => DownloadSubtitleTrackSelection.NotRequested,
            (true, null) => DownloadSubtitleTrackSelection.AllTracks,
            (true, { Length: 0 }) => DownloadSubtitleTrackSelection.NoTracksSelected,
            _ => DownloadSubtitleTrackSelection.SelectedTracks
        };

    private const string AudioKey = "downloadAudio";
    private const string VideoKey = "downloadVideo";
    private const string DanmakuKey = "downloadDanmaku";
    private const string SubtitleKey = "downloadSubtitle";
    private const string CoverKey = "downloadCover";
    private const string AudioAlias = "audio";
    private const string VideoAlias = "video";
    private const string DanmakuAlias = "danmaku";
    private const string SubtitleAlias = "subtitle";
    private const string CoverAlias = "cover";

    public static DownloadContentSelection All { get; } = new(
        Audio: true,
        Video: true,
        Danmaku: true,
        Subtitle: true,
        Cover: true);

    public static DownloadContentSelection None { get; } = new(
        Audio: false,
        Video: false,
        Danmaku: false,
        Subtitle: false,
        Cover: false);

    public static DownloadContentSelection FromLegacyMap(
        IEnumerable<KeyValuePair<string, bool>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var map = values.ToImmutableDictionary(StringComparer.Ordinal);
        return new DownloadContentSelection(
            Read(map, AudioKey, AudioAlias),
            Read(map, VideoKey, VideoAlias),
            Read(map, DanmakuKey, DanmakuAlias),
            Read(map, SubtitleKey, SubtitleAlias),
            Read(map, CoverKey, CoverAlias));
    }

    public ImmutableDictionary<string, bool> ToLegacyMap()
    {
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [AudioKey] = Audio,
            [VideoKey] = Video,
            [DanmakuKey] = Danmaku,
            [SubtitleKey] = Subtitle,
            [CoverKey] = Cover
        }.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static bool Read(
        ImmutableDictionary<string, bool> values,
        string key,
        string legacyAlias)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : values.GetValueOrDefault(legacyAlias);
    }
}

public static class DownloadOutputClaims
{
    public static bool Overlap(
        DownloadContentSelection first,
        DownloadContentSelection second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.HasMedia && second.HasMedia
               || SubtitleClaimsOverlap(first, second)
               || DanmakuClaimsOverlap(first, second)
               || first.Cover && second.Cover;
    }

    private static bool SubtitleClaimsOverlap(
        DownloadContentSelection first,
        DownloadContentSelection second) =>
        // Subtitle file names are language-derived, so distinct track ids do not prove
        // distinct physical outputs.
        first.HasSubtitleAction && second.HasSubtitleAction;

    private static bool DanmakuClaimsOverlap(
        DownloadContentSelection first,
        DownloadContentSelection second)
    {
        if (!first.Danmaku || !second.Danmaku)
        {
            return false;
        }

        var firstFormat = first.DanmakuOutputFormat ?? AllDanmakuFormats;
        var secondFormat = second.DanmakuOutputFormat ?? AllDanmakuFormats;
        return (firstFormat & secondFormat) != DownloadDanmakuOutputFormat.None;
    }

    private const DownloadDanmakuOutputFormat AllDanmakuFormats =
        DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml;
}
