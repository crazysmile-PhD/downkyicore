using System.Linq;
using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Services.Download;

internal static class DownloadAudioSelection
{
    public static PlayUrlDashVideo? Select(int audioCodecId, PlayUrl? playUrl)
    {
        var dash = playUrl?.Dash;
        if (dash == null)
        {
            return null;
        }

        var selected = dash.Audio?.FirstOrDefault(item => item.Id == audioCodecId);
        if (audioCodecId == 30250)
        {
            var dolbyAudio = dash.Dolby?.Audio;
            return dolbyAudio is { Count: > 0 } ? dolbyAudio[0] : selected;
        }

        if (audioCodecId == 30251)
        {
            var flacAudio = dash.Flac?.Audio;
            return HasMediaAddress(flacAudio) ? flacAudio : selected;
        }

        return selected;
    }

    public static bool HasAnyAudio(PlayUrlDash? dash)
    {
        return dash?.Audio is { Count: > 0 } ||
               dash?.Dolby?.Audio is { Count: > 0 } ||
               HasMediaAddress(dash?.Flac?.Audio);
    }

    private static bool HasMediaAddress(PlayUrlDashVideo? media)
    {
        return media != null &&
               (!string.IsNullOrWhiteSpace(media.BaseAddress) ||
                media.BackupUrl.Any(url => !string.IsNullOrWhiteSpace(url)));
    }
}
