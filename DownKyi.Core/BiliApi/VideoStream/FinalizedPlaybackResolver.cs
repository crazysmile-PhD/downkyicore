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
        ArgumentNullException.ThrowIfNull(primary);
        var sources = new[] { primary, supplement }.OfType<PlayUrl>().ToArray();
        selected = SelectSingleSource(sources, selection);
        return selected != null;
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
        return TrySelect(
            primary,
            supplement,
            new FinalizedPlaybackSelection(
                requestedQuality,
                requestedCodecId,
                requestedAudioId,
                requestedStreamKind,
                requireVideo),
            out selected);
    }

    private static PlayUrl? SelectSingleSource(
        PlayUrl[] sources,
        FinalizedPlaybackSelection selection)
    {
        var source = sources.FirstOrDefault(candidate => HasRequestedPlayback(
            candidate,
            selection.VideoQuality,
            selection.VideoCodecId,
            selection.AudioId,
            selection.StreamKind,
            selection.RequireVideo));
        return source == null
            ? null
            : SelectFromSource(source, selection);
    }

    private static PlayUrl? SelectFromSource(
        PlayUrl source,
        FinalizedPlaybackSelection selection)
    {
        var sourceAvailability = PlayUrlAvailability.From(source);
        var selectedKind = !selection.RequireVideo
            ? PlayUrlStreamKind.Dash
            : selection.StreamKind
              ?? (sourceAvailability.Video.Any(candidate =>
                  candidate.Quality == selection.VideoQuality
                  && (selection.VideoCodecId == null
                      || candidate.CodecId == selection.VideoCodecId)
                  && candidate.StreamKind == PlayUrlStreamKind.Dash)
                  ? PlayUrlStreamKind.Dash
                  : PlayUrlStreamKind.Durl);
        return selectedKind == PlayUrlStreamKind.Durl
            ? SelectDurl(source)
            : SelectDash(source, selection);
    }

    private static PlayUrl SelectDurl(PlayUrl source)
    {
        source.Dash = new PlayUrlDash();
        return source;
    }

    private static PlayUrl? SelectDash(
        PlayUrl source,
        FinalizedPlaybackSelection selection)
    {
        var requestedDash = source.Dash.Video
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(video => video.Id == selection.VideoQuality)
            .Where(video => selection.VideoCodecId == null
                || video.CodecId == selection.VideoCodecId)
            .ToArray();
        return (selection.RequireVideo, requestedDash.Length) switch
        {
            (true, 0) => null,
            _ => RetainDash(source, requestedDash, selection.RequireVideo)
        };
    }

    private static PlayUrl RetainDash(
        PlayUrl source,
        IReadOnlyList<PlayUrlDashVideo> requestedVideo,
        bool requireVideo)
    {
        source.Durl = [];
        source.Dash.Video = requireVideo ? requestedVideo : [];
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
        return source;
    }
}
