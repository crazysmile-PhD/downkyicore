using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class BangumiPlaybackResolver
{
    private const string EmbeddedPlaybackMarker = "playurlSSRData";

    public static bool ShouldTryWebPageFallback(PlayUrl playUrl, int requestedQuality)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        var actualQuality = GetHighestActualQuality(playUrl);
        var advertisedQuality = (playUrl.SupportFormats ?? [])
            .Select(format => format.Quality)
            .Concat(playUrl.AcceptQuality ?? [])
            .DefaultIfEmpty()
            .Max();

        return actualQuality > 0
               && actualQuality <= 64
               && requestedQuality > actualQuality
               && advertisedQuality > actualQuality;
    }

    public static int GetHighestActualQuality(PlayUrl playUrl)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        return playUrl.Dash.Video
            .Select(video => video.Id)
            .Append(playUrl.Durl.Count > 0 ? playUrl.Quality : 0)
            .DefaultIfEmpty()
            .Max();
    }

    public static bool TryParseEmbeddedPayload(
        string webpage,
        string operationName,
        out PlayUrl? playUrl,
        out string? playDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        playUrl = null;
        playDetail = null;
        var json = ExtractAssignedJsonObject(webpage, EmbeddedPlaybackMarker);
        if (json == null)
        {
            return false;
        }

        try
        {
            var current = JObject.Parse(json);
            current = UnwrapObject(current, "raw");
            current = UnwrapObject(current, "data");
            current = UnwrapObject(current, "result");

            var result = current.ToObject<BangumiPlayUrlV2Result>();
            if (result == null)
            {
                return false;
            }

            playDetail = result.PlayCheck?.PlayDetail;
            playUrl = BangumiPlayUrlV2Contract.SelectPayload(
                new BangumiPlayUrlV2Origin { Result = result },
                operationName);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (BilibiliApiResponseException)
        {
            return false;
        }
    }

    private static JObject UnwrapObject(JObject current, string propertyName)
    {
        return current[propertyName] is JObject nested ? nested : current;
    }

    private static string? ExtractAssignedJsonObject(string source, string marker)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var searchIndex = 0;
        while (searchIndex < source.Length)
        {
            var markerIndex = source.IndexOf(marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return null;
            }

            var assignmentIndex = markerIndex + marker.Length;
            while (assignmentIndex < source.Length && char.IsWhiteSpace(source[assignmentIndex]))
            {
                assignmentIndex++;
            }

            if (assignmentIndex < source.Length && source[assignmentIndex] == '=')
            {
                return ExtractJsonObject(source, assignmentIndex + 1);
            }

            searchIndex = markerIndex + marker.Length;
        }

        return null;
    }

    private static string? ExtractJsonObject(string source, int searchStart)
    {
        var objectStart = source.IndexOf('{', searchStart);
        if (objectStart < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = objectStart; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return source[objectStart..(index + 1)];
            }
        }

        return null;
    }
}
