using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class BangumiPlaybackResolver
{
    private const string EmbeddedPlaybackMarker = "playurlSSRData";

    public static PlayUrl CombineDiscovery(
        PlayUrl primary,
        PlayUrl supplement,
        int? preferredDurlQuality = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(supplement);
        var durlSource = new[] { primary, supplement }
            .FirstOrDefault(source => source.Quality == preferredDurlQuality
                && PlayUrlAvailability.HasUsableDurl(source))
            ?? new[] { primary, supplement }
                .FirstOrDefault(PlayUrlAvailability.HasUsableDurl);
        var firstVideoSource = PlayUrlAvailability.From(primary).Video.Count > 0
            ? primary : supplement;
        var combined = new PlayUrl
        {
            Quality = durlSource?.Quality ?? firstVideoSource.Quality,
            VideoCodecid = durlSource?.VideoCodecid ?? firstVideoSource.VideoCodecid,
            Durl = durlSource?.Durl ?? [],
            Dash = new PlayUrlDash
            {
                Duration = primary.Dash.Duration > 0
                    ? primary.Dash.Duration : supplement.Dash.Duration,
                Video = primary.Dash.Video.Concat(supplement.Dash.Video)
                    .Where(PlayUrlAvailability.HasUsableAddress)
                    .DistinctBy(stream => (stream.Id, stream.CodecId))
                    .ToArray(),
                Audio = primary.Dash.Audio.Concat(supplement.Dash.Audio)
                    .Where(PlayUrlAvailability.HasUsableAddress)
                    .DistinctBy(stream => stream.Id)
                    .ToArray(),
                Dolby = CombineDolby(primary, supplement),
                Flac = PlayUrlAvailability.HasUsableAddress(primary.Dash.Flac?.Audio)
                    ? primary.Dash.Flac : PlayUrlAvailability.HasUsableAddress(supplement.Dash.Flac?.Audio)
                        ? supplement.Dash.Flac : null
            },
            SupportFormats = primary.SupportFormats.Concat(supplement.SupportFormats)
                .DistinctBy(format => format.Quality).ToArray(),
            AcceptQuality = primary.AcceptQuality.Concat(supplement.AcceptQuality)
                .Distinct().ToArray()
        };
        combined.Availability = PlayUrlAvailability.From(combined);
        return combined;
    }

    private static PlayUrlDashDolby? CombineDolby(PlayUrl primary, PlayUrl supplement)
    {
        var audio = (primary.Dash.Dolby?.Audio ?? [])
            .Concat(supplement.Dash.Dolby?.Audio ?? [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .DistinctBy(stream => stream.Id)
            .ToArray();
        return audio.Length == 0 ? null : new PlayUrlDashDolby { Audio = audio };
    }

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

    public static bool HasRequestedPlayback(
        PlayUrl playUrl,
        int requestedQuality,
        int? requestedCodecId = null,
        int? requestedAudioId = null,
        PlayUrlStreamKind? requestedStreamKind = null,
        bool requireVideo = true)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        if (!requireVideo && requestedAudioId == null)
        {
            return false;
        }

        var availability = PlayUrlAvailability.From(playUrl);
        if (requireVideo && !availability.Video.Any(candidate =>
                candidate.Quality == requestedQuality
                && (requestedCodecId == null || candidate.CodecId == requestedCodecId)
                && (requestedStreamKind == null || candidate.StreamKind == requestedStreamKind)))
        {
            return false;
        }

        return requestedAudioId == null
               || availability.Audio.Contains(requestedAudioId.Value);
    }

    public static bool TrySelectDownloadPlayback(
        PlayUrl primary,
        PlayUrl? supplement,
        FinalizedPlaybackSelection selection,
        out PlayUrl? selected)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return TrySelectDownloadPlayback(
            primary,
            supplement,
            selection.VideoQuality,
            selection.VideoCodecId,
            selection.AudioId,
            selection.StreamKind,
            selection.RequireVideo,
            out selected);
    }

    public static bool TrySelectDownloadPlayback(
        PlayUrl primary,
        PlayUrl? supplement,
        int requestedQuality,
        int? requestedCodecId,
        int? requestedAudioId,
        PlayUrlStreamKind? requestedStreamKind,
        out PlayUrl? selected)
    {
        return TrySelectDownloadPlayback(
            primary,
            supplement,
            requestedQuality,
            requestedCodecId,
            requestedAudioId,
            requestedStreamKind,
            requireVideo: true,
            out selected);
    }

    public static bool TrySelectDownloadPlayback(
        PlayUrl primary,
        PlayUrl? supplement,
        int requestedQuality,
        int? requestedCodecId,
        int? requestedAudioId,
        PlayUrlStreamKind? requestedStreamKind,
        bool requireVideo,
        out PlayUrl? selected)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var source = HasRequestedPlayback(
                primary,
                requestedQuality,
                requestedCodecId,
                requestedAudioId,
                requestedStreamKind,
                requireVideo)
            ? primary
            : supplement != null && HasRequestedPlayback(
                supplement,
                requestedQuality,
                requestedCodecId,
                requestedAudioId,
                requestedStreamKind,
                requireVideo)
                ? supplement
                : null;
        if (source == null)
        {
            return TrySelectSplitSources(
                primary,
                supplement,
                requestedQuality,
                requestedCodecId,
                requestedAudioId,
                requestedStreamKind,
                requireVideo,
                out selected);
        }

        var sourceAvailability = PlayUrlAvailability.From(source);
        var selectedKind = !requireVideo
            ? PlayUrlStreamKind.Dash
            : requestedStreamKind
                           ?? (sourceAvailability.Video.Any(candidate =>
                               candidate.Quality == requestedQuality
                               && (requestedCodecId == null
                                   || candidate.CodecId == requestedCodecId)
                               && candidate.StreamKind == PlayUrlStreamKind.Dash)
                               ? PlayUrlStreamKind.Dash
                               : PlayUrlStreamKind.Durl);
        if (selectedKind == PlayUrlStreamKind.Durl)
        {
            if (requestedAudioId == null)
            {
                source.Dash = new PlayUrlDash();
            }
            else
            {
                source.Dash.Video = [];
                KeepRequestedAudio(source.Dash, requestedAudioId);
            }
            selected = source;
            return true;
        }

        var requestedDash = source.Dash.Video
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(video => video.Id == requestedQuality)
            .Where(video => requestedCodecId == null || video.CodecId == requestedCodecId)
            .ToArray();
        if (requireVideo && requestedDash.Length == 0)
        {
            selected = null;
            return false;
        }

        source.Durl = [];
        source.Dash.Video = requireVideo ? requestedDash : [];
        KeepRequestedAudio(source.Dash, requestedAudioId);

        selected = source;
        return true;
    }

    private static void KeepRequestedAudio(PlayUrlDash dash, int? requestedAudioId)
    {
        dash.Audio = dash.Audio
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(audio => requestedAudioId == null || audio.Id == requestedAudioId)
            .ToArray();
        var dolbyAudio = (dash.Dolby?.Audio ?? [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(audio => requestedAudioId == null || audio.Id == requestedAudioId)
            .ToArray();
        dash.Dolby = dolbyAudio.Length == 0
            ? null
            : new PlayUrlDashDolby { Audio = dolbyAudio };
        dash.Flac = PlayUrlAvailability.HasUsableAddress(dash.Flac?.Audio)
                    && (requestedAudioId == null
                        || dash.Flac?.Audio?.Id == requestedAudioId)
            ? dash.Flac
            : null;
    }

    private static bool TrySelectSplitSources(
        PlayUrl primary,
        PlayUrl? supplement,
        int requestedQuality,
        int? requestedCodecId,
        int? requestedAudioId,
        PlayUrlStreamKind? requestedStreamKind,
        bool requireVideo,
        out PlayUrl? selected)
    {
        selected = null;
        if (supplement == null || !requireVideo || requestedAudioId == null)
        {
            return false;
        }

        var videoKind = ResolveSplitVideoKind(
            primary, supplement, requestedQuality, requestedCodecId,
            requestedStreamKind);
        var videoSource = new[] { primary, supplement }.FirstOrDefault(source =>
            HasRequestedPlayback(source, requestedQuality, requestedCodecId,
                requestedAudioId: null, requestedStreamKind: videoKind));
        var audioSource = new[] { primary, supplement }.FirstOrDefault(source =>
            HasRequestedPlayback(source, requestedQuality, requestedCodecId,
                requestedAudioId, PlayUrlStreamKind.Dash, requireVideo: false));
        if (videoSource == null || audioSource == null)
        {
            return false;
        }

        var audio = audioSource.Dash.Audio
            .Where(PlayUrlAvailability.HasUsableAddress)
            .FirstOrDefault(stream => stream.Id == requestedAudioId);
        var dolby = (audioSource.Dash.Dolby?.Audio ?? [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .FirstOrDefault(stream => stream.Id == requestedAudioId);
        var flac = PlayUrlAvailability.HasUsableAddress(audioSource.Dash.Flac?.Audio)
                   && audioSource.Dash.Flac?.Audio?.Id == requestedAudioId
            ? audioSource.Dash.Flac.Audio
            : null;
        selected = new PlayUrl
        {
            Quality = requestedQuality,
            VideoCodecid = videoKind == PlayUrlStreamKind.Durl
                ? videoSource.VideoCodecid : requestedCodecId ?? videoSource.VideoCodecid,
            Durl = videoKind == PlayUrlStreamKind.Durl ? videoSource.Durl : [],
            Dash = new PlayUrlDash
            {
                Duration = videoSource.Dash.Duration > 0
                    ? videoSource.Dash.Duration : audioSource.Dash.Duration,
                Video = videoKind == PlayUrlStreamKind.Durl ? [] : videoSource.Dash.Video
                    .Where(PlayUrlAvailability.HasUsableAddress)
                    .Where(stream => stream.Id == requestedQuality
                        && (requestedCodecId == null || stream.CodecId == requestedCodecId))
                    .ToArray(),
                Audio = audio == null ? [] : [audio],
                Dolby = dolby == null ? null : new PlayUrlDashDolby { Audio = [dolby] },
                Flac = flac == null ? null : new PlayUrlDashFlac { Audio = flac }
            }
        };
        selected.Availability = PlayUrlAvailability.From(selected);
        return true;
    }

    private static PlayUrlStreamKind ResolveSplitVideoKind(
        PlayUrl primary,
        PlayUrl supplement,
        int requestedQuality,
        int? requestedCodecId,
        PlayUrlStreamKind? requestedStreamKind)
    {
        if (requestedStreamKind is { } kind)
        {
            return kind;
        }

        return new[] { primary, supplement }.Any(source =>
            PlayUrlAvailability.From(source).Video.Any(video =>
                video.Quality == requestedQuality
                && (requestedCodecId == null || video.CodecId == requestedCodecId)
                && video.StreamKind == PlayUrlStreamKind.Dash))
            ? PlayUrlStreamKind.Dash : PlayUrlStreamKind.Durl;
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
