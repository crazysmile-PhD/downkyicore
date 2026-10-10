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

    public DownloadActionClaims ActionClaims => DownloadActionClaims.From(this);

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

[Flags]
public enum DownloadActionClaim
{
    None = 0,
    Media = 1,
    Subtitle = 2,
    DanmakuAss = 4,
    DanmakuXml = 8,
    Cover = 16,
    Nfo = 32
}

public readonly record struct DownloadActionClaims(DownloadActionClaim Value)
{
    public static DownloadActionClaims None { get; } = new(DownloadActionClaim.None);

    public static DownloadActionClaims Media { get; } = new(DownloadActionClaim.Media);

    public static DownloadActionClaims Subtitle { get; } = new(DownloadActionClaim.Subtitle);

    public static DownloadActionClaims Cover { get; } = new(DownloadActionClaim.Cover);

    public static DownloadActionClaims Nfo { get; } = new(DownloadActionClaim.Nfo);

    public bool HasAny => Value != DownloadActionClaim.None;

    public DownloadDanmakuOutputFormat DanmakuFormats
    {
        get
        {
            var formats = DownloadDanmakuOutputFormat.None;
            if (Contains(DownloadActionClaim.DanmakuAss))
            {
                formats |= DownloadDanmakuOutputFormat.Ass;
            }

            if (Contains(DownloadActionClaim.DanmakuXml))
            {
                formats |= DownloadDanmakuOutputFormat.Xml;
            }

            return formats;
        }
    }

    public bool Contains(DownloadActionClaim claim) => (Value & claim) == claim;

    public bool Overlaps(DownloadActionClaims other) =>
        (Value & other.Value) != DownloadActionClaim.None;

    public DownloadActionClaims Union(DownloadActionClaims other) =>
        new(Value | other.Value);

    public DownloadContentSelection SubtractFrom(DownloadContentSelection requestedContent)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        var remaining = requestedContent;
        if (Contains(DownloadActionClaim.Media) && requestedContent.HasMedia)
        {
            remaining = remaining with
            {
                Audio = false,
                Video = false,
                MediaKind = DownloadMediaKind.None
            };
        }

        if (Contains(DownloadActionClaim.Subtitle) && requestedContent.HasSubtitleAction)
        {
            remaining = remaining with
            {
                Subtitle = false,
                SelectedSubtitleTrackIds = null,
                DefaultSubtitleTrackId = null
            };
        }

        if (requestedContent.Danmaku)
        {
            var requestedFormats = requestedContent.DanmakuOutputFormat ?? AllDanmakuFormats;
            var remainingFormats = requestedFormats & ~DanmakuFormats;
            if (remainingFormats != requestedFormats)
            {
                remaining = remaining with
                {
                    Danmaku = remainingFormats != DownloadDanmakuOutputFormat.None,
                    DanmakuOutputFormat = remainingFormats == DownloadDanmakuOutputFormat.None
                        ? null
                        : remainingFormats
                };
            }
        }

        if (Contains(DownloadActionClaim.Cover) && requestedContent.Cover)
        {
            remaining = remaining with { Cover = false };
        }

        return remaining;
    }

    public static DownloadActionClaims From(DownloadContentSelection content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var claims = DownloadActionClaim.None;
        if (content.HasMedia)
        {
            claims |= DownloadActionClaim.Media;
        }

        // Subtitle file names are language-derived, so distinct track ids do not prove
        // distinct physical outputs.
        if (content.HasSubtitleAction)
        {
            claims |= DownloadActionClaim.Subtitle;
        }

        if (content.Danmaku)
        {
            claims |= FromDanmaku(content.DanmakuOutputFormat ?? AllDanmakuFormats).Value;
        }

        if (content.Cover)
        {
            claims |= DownloadActionClaim.Cover;
        }

        return new DownloadActionClaims(claims);
    }

    public static DownloadActionClaims From(
        DownloadContentSelection content,
        bool includesNfo)
    {
        var claims = From(content);
        return includesNfo ? claims.Union(Nfo) : claims;
    }

    public static DownloadActionClaims FromDanmaku(DownloadDanmakuOutputFormat formats)
    {
        var claims = DownloadActionClaim.None;
        if (formats.HasFlag(DownloadDanmakuOutputFormat.Ass))
        {
            claims |= DownloadActionClaim.DanmakuAss;
        }

        if (formats.HasFlag(DownloadDanmakuOutputFormat.Xml))
        {
            claims |= DownloadActionClaim.DanmakuXml;
        }

        return new DownloadActionClaims(claims);
    }

    private const DownloadDanmakuOutputFormat AllDanmakuFormats =
        DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml;
}
