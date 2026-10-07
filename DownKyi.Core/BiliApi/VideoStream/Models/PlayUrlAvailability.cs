namespace DownKyi.Core.BiliApi.VideoStream.Models;

public enum PlayUrlStreamKind
{
    Dash,
    Durl
}

public sealed record PlayUrlVideoAvailability(
    int Quality,
    int CodecId,
    PlayUrlStreamKind StreamKind,
    string Description);

public sealed record PlayUrlAvailability(
    IReadOnlyList<PlayUrlVideoAvailability> Video,
    IReadOnlyList<int> Audio)
{
    public static PlayUrlAvailability From(PlayUrl playUrl)
    {
        ArgumentNullException.ThrowIfNull(playUrl);

        var video = playUrl.Dash.Video
            .Where(HasUsableAddress)
            .Select(stream => new PlayUrlVideoAvailability(
                stream.Id,
                stream.CodecId,
                PlayUrlStreamKind.Dash,
                GetDescription(playUrl, stream.Id)))
            .ToList();
        if (HasUsableDurl(playUrl))
        {
            video.Add(new PlayUrlVideoAvailability(
                playUrl.Quality,
                playUrl.VideoCodecid,
                PlayUrlStreamKind.Durl,
                GetDescription(playUrl, playUrl.Quality)));
        }

        var audio = playUrl.Dash.Audio
            .Where(HasUsableAddress)
            .Select(stream => stream.Id)
            .Concat((playUrl.Dash.Dolby?.Audio ?? [])
                .Where(HasUsableAddress)
                .Select(stream => stream.Id))
            .Concat(HasUsableAddress(playUrl.Dash.Flac?.Audio)
                ? [playUrl.Dash.Flac!.Audio!.Id]
                : [])
            .Where(id => id > 0)
            .Distinct()
            .OrderByDescending(id => id)
            .ToArray();

        return new PlayUrlAvailability(
            video
                .Where(stream => stream.Quality > 0 && stream.CodecId > 0)
                .DistinctBy(stream => (
                    stream.Quality,
                    stream.CodecId,
                    stream.StreamKind))
                .OrderByDescending(stream => stream.Quality)
                .ThenBy(stream => stream.CodecId)
                .ThenBy(stream => stream.StreamKind)
                .ToArray(),
            audio);
    }

    public static bool HasUsableAddress(PlayUrlDashVideo? media)
    {
        return media != null
               && (!string.IsNullOrWhiteSpace(media.BaseAddress)
                   || media.BackupUrl.Any(address => !string.IsNullOrWhiteSpace(address)));
    }

    public static bool HasUsableDurl(PlayUrl playUrl)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        return playUrl.Durl.Count > 0
               && !playUrl.Durl.GroupBy(durl => durl.Order).Any(group => group.Count() > 1)
               && playUrl.Durl.All(durl =>
                   !string.IsNullOrWhiteSpace(durl.SourceAddress)
                   || durl.BackupUrl.Any(address => !string.IsNullOrWhiteSpace(address)));
    }

    private static string GetDescription(PlayUrl playUrl, int quality)
    {
        return playUrl.SupportFormats
                   .FirstOrDefault(format => format.Quality == quality)
                   ?.NewDescription
               ?? string.Empty;
    }
}
