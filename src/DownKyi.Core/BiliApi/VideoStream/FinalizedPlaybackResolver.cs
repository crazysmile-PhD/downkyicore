using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class FinalizedPlaybackResolver
{
    public static bool TrySelect(
        PlayUrl primary,
        PlayUrl? supplement,
        FinalizedPlaybackSelection selection,
        out PlayUrl? selected)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.RequireVideo ? selection.VideoQuality <= 0 : selection.AudioId is not > 0)
        {
            selected = null;
            return false;
        }

        PlayUrl[] sources = supplement is null ? [primary] : [primary, supplement];
        selected = sources
            .Select(source => SelectFromSource(source, selection))
            .FirstOrDefault(candidate => candidate is not null)
            ?? SelectExactDashAcrossSources(sources, selection);
        return selected is not null;
    }

    private static PlayUrl? SelectFromSource(
        PlayUrl source,
        FinalizedPlaybackSelection selection)
    {
        var video = SelectVideo(source, selection);
        var kind = !selection.RequireVideo
            ? PlayUrlStreamKind.Dash
            : selection.StreamKind ?? (video.Length > 0 || selection.AudioId is not null
                ? PlayUrlStreamKind.Dash
                : PlayUrlStreamKind.Durl);

        return kind == PlayUrlStreamKind.Durl
            ? SelectDurl(source, selection)
            : SelectDash(source, source, selection, video);
    }

    private static PlayUrl? SelectDurl(
        PlayUrl source,
        FinalizedPlaybackSelection selection)
    {
        if (!selection.RequireVideo
            || source.Quality != selection.VideoQuality
            || source.VideoCodecid <= 0
            || (selection.VideoCodecId is { } codecId && source.VideoCodecid != codecId)
            || !PlayUrlAvailability.HasUsableDurl(source))
        {
            return null;
        }

        var selected = source.CopyForSelection();
        selected.Dash = new PlayUrlDash();
        selected.Durl = source.Durl.ToArray();
        selected.Availability = PlayUrlAvailability.From(selected);
        return selected;
    }

    private static PlayUrl? SelectDash(
        PlayUrl videoSource,
        PlayUrl audioSource,
        FinalizedPlaybackSelection selection,
        IReadOnlyList<PlayUrlDashVideo>? selectedVideo = null)
    {
        IReadOnlyList<PlayUrlDashVideo> video = selection.RequireVideo
            ? selectedVideo ?? SelectVideo(videoSource, selection)
            : [];
        if (selection.RequireVideo && video.Count == 0)
        {
            return null;
        }

        var audio = SelectAudio(audioSource, selection.AudioId);
        var dolbyAudio = SelectDolby(audioSource, selection.AudioId);
        var flacAudio = SelectFlac(audioSource, selection.AudioId);
        if (selection.AudioId is not null
            && audio.Length == 0 && dolbyAudio.Length == 0 && flacAudio is null)
        {
            return null;
        }

        var selected = videoSource.CopyForSelection();
        selected.Quality = selection.RequireVideo ? selection.VideoQuality : videoSource.Quality;
        selected.VideoCodecid = selection.VideoCodecId ?? videoSource.VideoCodecid;
        selected.Durl = [];
        selected.Dash = new PlayUrlDash
        {
            Duration = videoSource.Dash.Duration > 0
                ? videoSource.Dash.Duration
                : audioSource.Dash.Duration,
            Video = video,
            Audio = audio,
            Dolby = dolbyAudio.Length == 0 ? null : new PlayUrlDashDolby { Audio = dolbyAudio },
            Flac = flacAudio is null ? null : new PlayUrlDashFlac { Audio = flacAudio }
        };
        selected.Availability = PlayUrlAvailability.From(selected);
        return selected;
    }

    private static PlayUrl? SelectExactDashAcrossSources(
        PlayUrl[] sources,
        FinalizedPlaybackSelection selection)
    {
        if (sources.Length < 2
            || !selection.RequireVideo
            || selection.AudioId is null
            || selection.StreamKind == PlayUrlStreamKind.Durl)
        {
            return null;
        }

        var videoSource = sources.FirstOrDefault(source => SelectVideo(source, selection).Length > 0);
        var audioSource = sources.FirstOrDefault(source =>
            PlayUrlAvailability.From(source).Audio.Contains(selection.AudioId.Value));
        return videoSource is null || audioSource is null
            ? null
            : SelectDash(videoSource, audioSource, selection);
    }

    private static PlayUrlDashVideo[] SelectVideo(
        PlayUrl source,
        FinalizedPlaybackSelection selection) =>
        source.Dash.Video
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(stream => stream.CodecId > 0
                && stream.Id == selection.VideoQuality
                && (selection.VideoCodecId is null || stream.CodecId == selection.VideoCodecId))
            .ToArray();

    private static PlayUrlDashVideo[] SelectAudio(PlayUrl source, int? requestedId) =>
        source.Dash.Audio
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(stream => stream.Id > 0 && (requestedId is null || stream.Id == requestedId))
            .ToArray();

    private static PlayUrlDashVideo[] SelectDolby(PlayUrl source, int? requestedId) =>
        (source.Dash.Dolby?.Audio ?? [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Where(stream => stream.Id > 0 && (requestedId is null || stream.Id == requestedId))
            .ToArray();

    private static PlayUrlDashVideo? SelectFlac(PlayUrl source, int? requestedId) =>
        source.Dash.Flac?.Audio is { } flac
        && PlayUrlAvailability.HasUsableAddress(flac)
        && flac.Id > 0
        && (requestedId is null || flac.Id == requestedId)
            ? flac
            : null;

}
