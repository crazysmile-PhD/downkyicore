using System.Buffers;
using System.Globalization;

namespace DownKyi.Application.Media;

public abstract record BilibiliInput
{
    public abstract Uri CanonicalUri { get; }
}

public abstract record BilibiliMediaInput : BilibiliInput;

public sealed record BilibiliBvidInput : BilibiliMediaInput
{
    public BilibiliBvidInput(string bvid)
    {
        if (!BilibiliInputSyntax.IsBvid(bvid))
        {
            throw new ArgumentException("A valid Bilibili BV identifier is required.", nameof(bvid));
        }

        Bvid = bvid;
    }

    public string Bvid { get; }

    public override Uri CanonicalUri => new($"https://www.bilibili.com/video/{Bvid}");
}

public sealed record BilibiliAvidInput : BilibiliMediaInput
{
    public BilibiliAvidInput(long avid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(avid);
        Avid = avid;
    }

    public long Avid { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/video/av{Avid.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliBangumiSeasonInput : BilibiliMediaInput
{
    public BilibiliBangumiSeasonInput(long seasonId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seasonId);
        SeasonId = seasonId;
    }

    public long SeasonId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/bangumi/play/ss{SeasonId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliBangumiEpisodeInput : BilibiliMediaInput
{
    public BilibiliBangumiEpisodeInput(long episodeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        EpisodeId = episodeId;
    }

    public long EpisodeId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/bangumi/play/ep{EpisodeId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliBangumiMediaInput : BilibiliMediaInput
{
    public BilibiliBangumiMediaInput(long mediaId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaId);
        MediaId = mediaId;
    }

    public long MediaId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/bangumi/media/md{MediaId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliCheeseSeasonInput : BilibiliMediaInput
{
    public BilibiliCheeseSeasonInput(long seasonId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seasonId);
        SeasonId = seasonId;
    }

    public long SeasonId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/cheese/play/ss{SeasonId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliCheeseEpisodeInput : BilibiliMediaInput
{
    public BilibiliCheeseEpisodeInput(long episodeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        EpisodeId = episodeId;
    }

    public long EpisodeId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/cheese/play/ep{EpisodeId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliFavoritesInput : BilibiliInput
{
    public BilibiliFavoritesInput(long mediaId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaId);
        MediaId = mediaId;
    }

    public long MediaId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/list/ml{MediaId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliUserSpaceInput : BilibiliInput
{
    public BilibiliUserSpaceInput(long mid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mid);
        Mid = mid;
    }

    public long Mid { get; }

    public override Uri CanonicalUri => new(
        $"https://space.bilibili.com/{Mid.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliPublicationInput : BilibiliInput
{
    public BilibiliPublicationInput(long mid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mid);
        Mid = mid;
    }

    public long Mid { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/list/{Mid.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record BilibiliSeriesInput : BilibiliInput
{
    public BilibiliSeriesInput(long mid, long seriesId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seriesId);
        Mid = mid;
        SeriesId = seriesId;
    }

    public long Mid { get; }

    public long SeriesId { get; }

    public override Uri CanonicalUri => new(
        $"https://www.bilibili.com/list/{Mid.ToString(CultureInfo.InvariantCulture)}" +
        $"?sid={SeriesId.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record VideoNavigationPayload(
    BilibiliMediaInput Input,
    bool SuppressDuplicate = false);

internal static class BilibiliInputSyntax
{
    private static readonly SearchValues<char> BvidCharacters =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    public static bool IsBvid(string? input)
    {
        return input is { Length: 12 }
            && input.StartsWith("BV", StringComparison.Ordinal)
            && input.AsSpan(2).IndexOfAnyExcept(BvidCharacters) < 0;
    }

    public static bool TryParsePositivePrefixedId(
        string input,
        string prefix,
        out long id)
    {
        id = -1;
        return input.Length > prefix.Length
            && input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(
                input.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id)
            && id > 0;
    }
}
