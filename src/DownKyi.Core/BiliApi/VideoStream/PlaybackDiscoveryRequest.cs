using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

/// <summary>
/// Describes the media that playback discovery must make available before it can
/// accept the webpage response without querying the API.
/// </summary>
public sealed record PlaybackDiscoveryRequest(
    int PreferredVideoQuality,
    int PreferredAudioQuality,
    bool RequireVideo,
    bool RequireAudio)
{
    public bool NeedsApiSupplement(PlayUrlAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        var videoMatch = PlaybackQualityCatalog.SelectVideoQuality(
            availability.Video.Select(video => video.Quality),
            PreferredVideoQuality);
        if (RequireVideo && !videoMatch.SatisfiesWithoutConfirmation)
        {
            return true;
        }

        if (!RequireAudio)
        {
            return false;
        }

        var audioMatch = PlaybackQualityCatalog.SelectAudioQuality(
            availability.Audio,
            PreferredAudioQuality);
        if (audioMatch.SatisfiesWithoutConfirmation)
        {
            return false;
        }

        var combinedStreamMatch = PlaybackQualityCatalog.SelectVideoQuality(
            availability.Video
                .Where(video => video.StreamKind == PlayUrlStreamKind.Durl)
                .Select(video => video.Quality),
            PreferredVideoQuality);
        return !RequireVideo || !combinedStreamMatch.SatisfiesWithoutConfirmation;
    }
}
