using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class PlaybackSourceProvenance
{
    public static PlayUrl Mark(PlayUrl playback, PlayUrlResolutionSource source)
    {
        ArgumentNullException.ThrowIfNull(playback);
        foreach (var stream in Streams(playback))
        {
            stream.ResolutionSource = source;
        }

        return playback;
    }

    public static PlayUrlResolutionSource FromRetainedStreams(
        PlayUrl playback,
        PlayUrlResolutionSource fallback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        var sources = Streams(playback)
            .Where(PlayUrlAvailability.HasUsableAddress)
            .Select(stream => stream.ResolutionSource)
            .OfType<PlayUrlResolutionSource>()
            .Distinct()
            .ToArray();
        return sources switch
        {
            [] => fallback,
            [var source] => source,
            _ => PlayUrlResolutionSource.Mixed
        };
    }

    private static IEnumerable<PlayUrlDashVideo> Streams(PlayUrl playback) =>
        playback.Dash.Video
            .Concat(playback.Dash.Audio)
            .Concat(playback.Dash.Dolby?.Audio ?? [])
            .Concat(playback.Dash.Flac?.Audio is { } flac ? [flac] : []);
}
