using DownKyi.Core.BiliApi.BiliUtils;
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
               && actualQuality <= PlaybackQualityCatalog.Maximum720PQuality
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

    public static BangumiPlaybackFallbackResult CombinePlayback(
        PlayUrl primary,
        PlayUrl supplement)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(supplement);
        var primaryQuality = GetHighestActualQuality(primary);
        var supplementDashQuality = supplement.Dash.Video
            .Select(video => video.Id)
            .DefaultIfEmpty()
            .Max();
        var supplementDurlQuality = supplement.Durl.Count > 0
            ? supplement.Quality
            : 0;
        if (supplementDurlQuality > primaryQuality
            && supplementDurlQuality > supplementDashQuality)
        {
            return new BangumiPlaybackFallbackResult(
                supplement,
                "embedded-playback-selected-durl");
        }

        primary.Dash.Video = primary.Dash.Video
            .Concat(supplement.Dash.Video)
            .GroupBy(video => (video.Id, video.CodecId))
            .Select(group => group.First())
            .OrderByDescending(video => video.Id)
            .ThenBy(video => video.CodecId)
            .ToArray();
        primary.Dash.Audio = primary.Dash.Audio
            .Concat(supplement.Dash.Audio)
            .GroupBy(audio => audio.Id)
            .Select(group => group.First())
            .OrderByDescending(audio => audio.Id)
            .ToArray();
        if (primary.Dash.Dolby?.Audio is not { Count: > 0 }
            && supplement.Dash.Dolby?.Audio is { Count: > 0 })
        {
            primary.Dash.Dolby = supplement.Dash.Dolby;
        }

        if (primary.Dash.Flac?.Audio == null
            && supplement.Dash.Flac?.Audio != null)
        {
            primary.Dash.Flac = supplement.Dash.Flac;
        }

        primary.SupportFormats = (primary.SupportFormats ?? [])
            .Concat(supplement.SupportFormats ?? [])
            .GroupBy(format => format.Quality)
            .Select(group => group.First())
            .OrderByDescending(format => format.Quality)
            .ToArray();
        primary.AcceptQuality = (primary.AcceptQuality ?? [])
            .Concat(supplement.AcceptQuality ?? [])
            .Distinct()
            .OrderByDescending(quality => quality)
            .ToArray();
        primary.Quality = Math.Max(primary.Quality, supplement.Quality);
        return new BangumiPlaybackFallbackResult(
            primary,
            "embedded-playback-merged");
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
            var root = JObject.Parse(json);
            var response = ParseEmbeddedEnvelope(root, operationName);
            playDetail = response.Result?.PlayCheck?.PlayDetail;
            playUrl = BangumiPlayUrlV2Contract.SelectPayload(response, operationName);
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

    private static BangumiPlayUrlV2Origin ParseEmbeddedEnvelope(
        JObject root,
        string operationName)
    {
        var current = root;
        var metadataNodes = new List<JObject> { root };
        if (current["raw"] is JObject raw)
        {
            current = raw;
            metadataNodes.Add(raw);
        }

        if (current["data"] is JObject data)
        {
            current = data;
            metadataNodes.Add(data);
        }

        var result = current["result"] is JObject nestedResult
            ? nestedResult
            : current;
        var metadata = metadataNodes.FirstOrDefault(node =>
                           node["code"] is { } code && !IsSuccessfulCode(code))
                       ?? metadataNodes.FirstOrDefault(node => node["code"] != null);
        var normalized = new JObject
        {
            ["result"] = result.DeepClone()
        };
        if (metadata?["code"] != null)
        {
            normalized["code"] = metadata["code"]!.DeepClone();
        }

        if (metadata?["message"] != null)
        {
            normalized["message"] = metadata["message"]!.DeepClone();
        }

        return BiliApiRequest.ParseJson<BangumiPlayUrlV2Origin>(
            normalized.ToString(Formatting.None),
            operationName);
    }

    private static bool IsSuccessfulCode(JToken code)
    {
        return code.Type == JTokenType.Integer
               && string.Equals(code.ToString(Formatting.None), "0", StringComparison.Ordinal);
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

internal sealed record BangumiPlaybackFallbackResult(
    PlayUrl PlayUrl,
    string Outcome);
