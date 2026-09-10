using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed record DownloadContentSelection(
    bool Audio,
    bool Video,
    bool Danmaku,
    bool Subtitle,
    bool Cover)
{
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
