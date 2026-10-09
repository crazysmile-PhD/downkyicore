using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class FinalizedPlaybackResolver
{
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
               || (requireVideo && requestedStreamKind == PlayUrlStreamKind.Durl)
               || availability.Audio.Contains(requestedAudioId.Value);
    }

    public static bool TrySelect(
        PlayUrl primary,
        PlayUrl? supplement,
        FinalizedPlaybackSelection selection,
        out PlayUrl? selected)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return TrySelect(
            primary,
            supplement,
            selection.VideoQuality,
            selection.VideoCodecId,
            selection.AudioId,
            selection.StreamKind,
            selection.RequireVideo,
            out selected);
    }

    public static bool TrySelect(
        PlayUrl primary,
        PlayUrl? supplement,
        int requestedQuality,
        int? requestedCodecId,
        int? requestedAudioId,
        PlayUrlStreamKind? requestedStreamKind,
        out PlayUrl? selected)
    {
        return TrySelect(
            primary,
            supplement,
            requestedQuality,
            requestedCodecId,
            requestedAudioId,
            requestedStreamKind,
            requireVideo: true,
            out selected);
    }

    public static bool TrySelect(
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
            selected = null;
            return false;
        }

        var sourceAvailability = PlayUrlAvailability.From(source);
        var selectedKind = !requireVideo
            ? PlayUrlStreamKind.Dash
            : requestedStreamKind
              ?? (sourceAvailability.Video.Any(candidate =>
                  candidate.Quality == requestedQuality
                  && (requestedCodecId == null || candidate.CodecId == requestedCodecId)
                  && candidate.StreamKind == PlayUrlStreamKind.Dash)
                  ? PlayUrlStreamKind.Dash
                  : PlayUrlStreamKind.Durl);
        if (selectedKind == PlayUrlStreamKind.Durl)
        {
            source.Dash = new PlayUrlDash();
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
        source.Dash.Audio = source.Dash.Audio
            .Where(PlayUrlAvailability.HasUsableAddress)
            .ToArray();
        var dolbyAudio = (source.Dash.Dolby?.Audio ?? [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .ToArray();
        source.Dash.Dolby = dolbyAudio.Length == 0
            ? null
            : new PlayUrlDashDolby { Audio = dolbyAudio };
        source.Dash.Flac = PlayUrlAvailability.HasUsableAddress(source.Dash.Flac?.Audio)
            ? source.Dash.Flac
            : null;

        selected = source;
        return true;
    }
}
