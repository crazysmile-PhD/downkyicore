using System.Collections.Immutable;

namespace DownKyi.Core.BiliApi.BiliUtils;

public enum PlaybackQualityMatchKind
{
    Unavailable,
    Exact,
    Higher,
    Lower
}

public sealed record PlaybackQualityMatch(
    int RequestedQuality,
    int? SelectedQuality,
    PlaybackQualityMatchKind Kind)
{
    public bool SatisfiesWithoutConfirmation =>
        Kind is PlaybackQualityMatchKind.Exact or PlaybackQualityMatchKind.Higher;

    public bool RequiresLowerQualityConfirmation => Kind == PlaybackQualityMatchKind.Lower;
}

public static class PlaybackQualityCatalog
{
    public const int MaximumProbeQuality = 127;
    public const int Maximum720PQuality = 74;

    private static readonly ImmutableArray<QualityOption> Resolutions =
    [
        new("360P 流畅", 16),
        new("480P 清晰", 32),
        new("720P 高清", 64),
        new("720P 60帧", 74),
        new("1080P 高清", 80),
        new("1080P 高码率", 112),
        new("1080P 60帧", 116),
        new("4K 超清", 120),
        new("HDR 真彩", 125),
        new("杜比视界", 126),
        new("超高清 8K", 127)
    ];

    private static readonly ImmutableArray<QualityOption> CodecIds =
    [
        new("H.264/AVC", 7),
        new("H.265/HEVC", 12),
        new("AV1", 13)
    ];

    private static readonly ImmutableArray<QualityOption> AudioQualities =
    [
        new("低质量", 30216),
        new("中质量", 30232),
        new("高质量", 30280),
        new("Dolby Atmos", 30250, SettingsId: 31250),
        new("Hi-Res无损", 30251, SettingsId: 31251)
    ];

    public static IReadOnlyList<Quality> GetResolutions()
    {
        return CreateMutableOptions(Resolutions);
    }

    public static IReadOnlyList<Quality> GetCodecIds()
    {
        return CreateMutableOptions(CodecIds);
    }

    public static IReadOnlyList<Quality> GetAudioQualities()
    {
        return CreateMutableOptions(AudioQualities);
    }

    public static IReadOnlyList<Quality> GetAudioPreferences()
    {
        return AudioQualities
            .Select(option => new Quality
            {
                Name = option.Name,
                Id = option.SettingsId ?? option.Id
            })
            .ToArray();
    }

    public static PlaybackQualityMatch SelectVideoQuality(
        IEnumerable<int> availableQualities,
        int requestedQuality)
    {
        return SelectQuality(Resolutions, availableQualities, requestedQuality);
    }

    public static PlaybackQualityMatch SelectAudioQuality(
        IEnumerable<int> availableQualities,
        int requestedQuality)
    {
        var normalizedRequestedQuality = AudioQualities
            .Where(option => (option.SettingsId ?? option.Id) == requestedQuality)
            .Select(option => option.Id)
            .DefaultIfEmpty(requestedQuality)
            .Single();
        return SelectQuality(
            AudioQualities,
            availableQualities,
            normalizedRequestedQuality);
    }

    private static PlaybackQualityMatch SelectQuality(
        ImmutableArray<QualityOption> catalog,
        IEnumerable<int> availableQualities,
        int requestedQuality)
    {
        ArgumentNullException.ThrowIfNull(availableQualities);
        var available = availableQualities
            .Where(quality => quality > 0)
            .Distinct()
            .ToHashSet();
        return (available.Count, available.Contains(requestedQuality)) switch
        {
            (0, _) => new PlaybackQualityMatch(
                requestedQuality,
                SelectedQuality: null,
                PlaybackQualityMatchKind.Unavailable),
            (_, true) => new PlaybackQualityMatch(
                requestedQuality,
                requestedQuality,
                PlaybackQualityMatchKind.Exact),
            _ => SelectNearestQuality(catalog, available, requestedQuality)
        };
    }

    private static PlaybackQualityMatch SelectNearestQuality(
        ImmutableArray<QualityOption> catalog,
        IReadOnlySet<int> available,
        int requestedQuality)
    {
        var ranked = catalog
            .Select((option, rank) => new RankedQuality(option.Id, rank))
            .ToArray();
        var requested = ranked
            .Where(quality => quality.Id == requestedQuality)
            .Select(quality => (RankedQuality?)quality)
            .SingleOrDefault();
        return requested switch
        {
            { } requestedRank => SelectRankedQuality(
                ranked,
                available,
                requestedQuality,
                requestedRank),
            null => SelectNumericQuality(available, requestedQuality)
        };
    }

    private static PlaybackQualityMatch SelectRankedQuality(
        IEnumerable<RankedQuality> ranked,
        IReadOnlySet<int> available,
        int requestedQuality,
        RankedQuality requested)
    {
        var candidates = ranked
            .Where(quality => available.Contains(quality.Id))
            .ToArray();
        var higher = candidates
            .Where(quality => quality.Rank > requested.Rank)
            .OrderBy(quality => quality.Rank)
            .Select(quality => new QualitySelection(
                quality.Id,
                PlaybackQualityMatchKind.Higher))
            .FirstOrDefault();
        var lower = candidates
            .Where(quality => quality.Rank < requested.Rank)
            .OrderByDescending(quality => quality.Rank)
            .Select(quality => new QualitySelection(
                quality.Id,
                PlaybackQualityMatchKind.Lower))
            .FirstOrDefault();
        return (higher ?? lower) switch
        {
            { } selected => new PlaybackQualityMatch(
                requestedQuality,
                selected.Quality,
                selected.Kind),
            null => SelectNumericQuality(available, requestedQuality)
        };
    }

    private static PlaybackQualityMatch SelectNumericQuality(
        IEnumerable<int> available,
        int requestedQuality)
    {
        var higher = available
            .Where(quality => quality > requestedQuality)
            .Order()
            .Cast<int?>()
            .FirstOrDefault();
        return higher switch
        {
            { } higherQuality => new PlaybackQualityMatch(
                requestedQuality,
                higherQuality,
                PlaybackQualityMatchKind.Higher),
            null => new PlaybackQualityMatch(
                requestedQuality,
                available.Max(),
                PlaybackQualityMatchKind.Lower)
        };
    }

    private static Quality[] CreateMutableOptions(ImmutableArray<QualityOption> options)
    {
        return options
            .Select(option => new Quality { Name = option.Name, Id = option.Id })
            .ToArray();
    }

    private readonly record struct QualityOption(string Name, int Id, int? SettingsId = null);

    private readonly record struct RankedQuality(int Id, int Rank);

    private sealed record QualitySelection(int Quality, PlaybackQualityMatchKind Kind);
}
