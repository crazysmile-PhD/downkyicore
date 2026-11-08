using System.Globalization;

namespace DownKyi.Application.Media;

public static class BilibiliInputParser
{
    public static BilibiliInput? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var value = input.Trim();
        return ParseBare(value) ?? ParseUri(value);
    }

    private static BilibiliInput? ParseBare(string value)
    {
        if (BilibiliInputSyntax.IsBvid(value))
        {
            return new BilibiliBvidInput(value);
        }

        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "av", out var avid))
        {
            return new BilibiliAvidInput(avid);
        }

        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ss", out var seasonId))
        {
            return new BilibiliBangumiSeasonInput(seasonId);
        }

        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ep", out var episodeId))
        {
            return new BilibiliBangumiEpisodeInput(episodeId);
        }

        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "md", out var mediaId))
        {
            return new BilibiliBangumiMediaInput(mediaId);
        }

        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ml", out var favoriteId))
        {
            return new BilibiliFavoritesInput(favoriteId);
        }

        if (value.StartsWith("uid:", StringComparison.OrdinalIgnoreCase)
            && BilibiliInputSyntax.TryParsePositivePrefixedId(value, "uid:", out var colonMid))
        {
            return new BilibiliUserSpaceInput(colonMid);
        }

        return BilibiliInputSyntax.TryParsePositivePrefixedId(value, "uid", out var mid)
            ? new BilibiliUserSpaceInput(mid)
            : null;
    }

    private static BilibiliInput? ParseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !TryGetSegments(uri, out var segments))
        {
            return null;
        }

        if (string.Equals(uri.IdnHost, "b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 1 && ParseBare(segments[0]) is BilibiliMediaInput media
                ? media
                : null;
        }

        if (string.Equals(uri.IdnHost, "space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUserSpacePath(segments);
        }

        if (!IsWebHost(uri.IdnHost))
        {
            return null;
        }

        segments = RemoveSupportedWrapper(segments);
        return ParseWebPath(uri, segments);
    }

    private static BilibiliUserSpaceInput? ParseUserSpacePath(string[] segments)
    {
        var supportedPath = segments.Length == 1
            || (segments.Length == 3
                && string.Equals(segments[1], "upload", StringComparison.Ordinal)
                && string.Equals(segments[2], "video", StringComparison.Ordinal));
        return supportedPath
            && long.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mid)
            && mid > 0
                ? new BilibiliUserSpaceInput(mid)
                : null;
    }

    private static BilibiliInput? ParseWebPath(Uri uri, string[] segments)
    {
        if (segments is ["video", var videoId])
        {
            return ParseVideoId(videoId);
        }

        if (segments is ["bangumi", "play", var bangumiId])
        {
            return ParseBangumiId(bangumiId);
        }

        if (segments is ["bangumi", "media", var mediaId]
            && BilibiliInputSyntax.TryParsePositivePrefixedId(mediaId, "md", out var parsedMediaId))
        {
            return new BilibiliBangumiMediaInput(parsedMediaId);
        }

        if (segments is ["cheese", "play", var cheeseId])
        {
            return ParseCheeseId(cheeseId);
        }

        if (segments is ["medialist", "detail", var detailId])
        {
            return ParseFavoritesId(detailId);
        }

        if (segments.Length is 3 or 4
            && string.Equals(segments[0], "medialist", StringComparison.Ordinal)
            && string.Equals(segments[1], "play", StringComparison.Ordinal)
            && (segments.Length == 3 || BilibiliInputSyntax.IsBvid(segments[3])))
        {
            return ParseFavoritesId(segments[2]);
        }

        if (segments is ["list", var listId])
        {
            return ParseList(uri, listId);
        }

        return null;
    }

    private static BilibiliInput? ParseVideoId(string value)
    {
        if (BilibiliInputSyntax.IsBvid(value))
        {
            return new BilibiliBvidInput(value);
        }

        return BilibiliInputSyntax.TryParsePositivePrefixedId(value, "av", out var avid)
            ? new BilibiliAvidInput(avid)
            : null;
    }

    private static BilibiliInput? ParseBangumiId(string value)
    {
        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ss", out var seasonId))
        {
            return new BilibiliBangumiSeasonInput(seasonId);
        }

        return BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ep", out var episodeId)
            ? new BilibiliBangumiEpisodeInput(episodeId)
            : null;
    }

    private static BilibiliInput? ParseCheeseId(string value)
    {
        if (BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ss", out var seasonId))
        {
            return new BilibiliCheeseSeasonInput(seasonId);
        }

        return BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ep", out var episodeId)
            ? new BilibiliCheeseEpisodeInput(episodeId)
            : null;
    }

    private static BilibiliFavoritesInput? ParseFavoritesId(string value)
    {
        return BilibiliInputSyntax.TryParsePositivePrefixedId(value, "ml", out var mediaId)
            ? new BilibiliFavoritesInput(mediaId)
            : null;
    }

    private static BilibiliInput? ParseList(Uri uri, string value)
    {
        if (ParseFavoritesId(value) is { } favorites)
        {
            return favorites;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mid)
            || mid <= 0
            || !TryGetSingleQueryValue(uri.Query, "sid", out var hasSeriesId, out var seriesValue))
        {
            return null;
        }

        if (!hasSeriesId)
        {
            return new BilibiliPublicationInput(mid);
        }

        return long.TryParse(seriesValue, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId)
            && seriesId > 0
                ? new BilibiliSeriesInput(mid, seriesId)
                : null;
    }

    private static string[] RemoveSupportedWrapper(string[] segments)
    {
        if (segments.Length > 1
            && string.Equals(segments[0], "s", StringComparison.Ordinal)
            && string.Equals(segments[1], "video", StringComparison.Ordinal))
        {
            return segments[1..];
        }

        return segments.Length > 1
            && IsLocaleSegment(segments[0])
            && IsKnownRouteRoot(segments[1])
                ? segments[1..]
                : segments;
    }

    private static bool IsLocaleSegment(string value)
    {
        return value is "zh-Hans" or "zh-Hant";
    }

    private static bool IsWebHost(string value)
    {
        return string.Equals(value, "bilibili.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "www.bilibili.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "m.bilibili.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRouteRoot(string value)
    {
        return value is "video" or "bangumi" or "cheese" or "medialist" or "list";
    }

    private static bool TryGetSegments(Uri uri, out string[] segments)
    {
        segments = [];
        var path = uri.AbsolutePath;
        if (path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var decoded = new string[encoded.Length];
        try
        {
            for (var index = 0; index < encoded.Length; index++)
            {
                decoded[index] = Uri.UnescapeDataString(encoded[index]);
                if (decoded[index].Contains('/', StringComparison.Ordinal)
                    || decoded[index].Contains('\\', StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        catch (UriFormatException)
        {
            return false;
        }

        segments = decoded;
        return true;
    }

    private static bool TryGetSingleQueryValue(
        string query,
        string name,
        out bool found,
        out string value)
    {
        found = false;
        value = string.Empty;
        try
        {
            foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = item.Split('=', 2);
                if (!string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found || pair.Length != 2)
                {
                    return false;
                }

                found = true;
                value = Uri.UnescapeDataString(pair[1]);
            }
        }
        catch (UriFormatException)
        {
            return false;
        }

        return true;
    }
}
