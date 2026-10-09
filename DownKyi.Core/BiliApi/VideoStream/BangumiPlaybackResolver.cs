using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class BangumiPlaybackResolver
{
    private const string EmbeddedPlaybackMarker = "playurlSSRData";

    public static PlayUrlAvailability DiscoverAvailability(
        PlayUrl primary,
        PlayUrl? supplement = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var primaryAvailability = PlayUrlAvailability.From(primary);
        if (supplement == null)
        {
            return primaryAvailability;
        }

        var supplementAvailability = PlayUrlAvailability.From(supplement);
        var video = primaryAvailability.Video
            .Concat(supplementAvailability.Video)
            .GroupBy(video => (video.Quality, video.CodecId, video.StreamKind))
            .Select(group => group.First())
            .OrderByDescending(video => video.Quality)
            .ThenBy(video => video.CodecId)
            .ToArray();
        var audio = primaryAvailability.Audio
            .Concat(supplementAvailability.Audio)
            .Distinct()
            .OrderByDescending(id => id)
            .ToArray();
        return new PlayUrlAvailability(video, audio);
    }

    public static bool HasActualQuality(PlayUrl playUrl, int quality)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        return PlayUrlAvailability.From(playUrl).Video.Any(video => video.Quality == quality);
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
        catch (PlaybackResourceUnavailableException)
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
