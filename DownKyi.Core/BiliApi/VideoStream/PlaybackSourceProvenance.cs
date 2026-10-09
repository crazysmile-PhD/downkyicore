using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class PlaybackSourceProvenance
{
    public static PlayUrl Mark(PlayUrl playback, PlayUrlResolutionSource source)
    {
        ArgumentNullException.ThrowIfNull(playback);
        foreach (var stream in playback.Dash.Video
                     .Concat(playback.Dash.Audio)
                     .Concat(playback.Dash.Dolby?.Audio ?? [])
                     .Concat(playback.Dash.Flac?.Audio is { } flac ? [flac] : [])
                     .Where(stream => stream != null))
        {
            stream.Source = source;
        }

        foreach (var segment in playback.Durl.Where(segment => segment != null))
        {
            segment.Source = source;
        }

        return playback;
    }

    public static PlayUrlResolutionSource FromRetainedStreams(
        PlayUrl playback,
        PlayUrlResolutionSource fallback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        var sources = playback.Dash.Video
            .Concat(playback.Dash.Audio)
            .Concat(playback.Dash.Dolby?.Audio ?? [])
            .Concat(playback.Dash.Flac?.Audio is { } flac ? [flac] : [])
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Select(stream => stream.Source)
            .Concat(PlayUrlAvailability.HasUsableDurl(playback)
                ? playback.Durl.Select(segment => segment.Source)
                : [])
            .Where(source => source != null)
            .Distinct()
            .ToArray();
        return sources.Length switch
        {
            0 => fallback,
            1 => sources[0]!.Value,
            _ => PlayUrlResolutionSource.Mixed
        };
    }
}
