using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;

namespace DownKyi.Services.Video;

internal enum VideoInputKind
{
    Unknown,
    Video,
    Bangumi,
    Cheese
}

internal static class VideoInputResolver
{
    public static VideoInputKind Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return VideoInputKind.Unknown;
        }

        if (ParseEntrance.IsAvUrl(input) ||
            ParseEntrance.IsBvUrl(input) ||
            ParseEntrance.IsAvId(input) ||
            ParseEntrance.IsBvId(input))
        {
            return VideoInputKind.Video;
        }

        if (ParseEntrance.IsBangumiSeasonUrl(input) ||
            ParseEntrance.IsBangumiEpisodeUrl(input) ||
            ParseEntrance.IsBangumiMediaUrl(input) ||
            ParseEntrance.IsBangumiSeasonId(input) ||
            ParseEntrance.IsBangumiEpisodeId(input) ||
            ParseEntrance.IsBangumiMediaId(input))
        {
            return VideoInputKind.Bangumi;
        }

        if (ParseEntrance.IsCheeseSeasonUrl(input) || ParseEntrance.IsCheeseEpisodeUrl(input))
        {
            return VideoInputKind.Cheese;
        }

        return VideoInputKind.Unknown;
    }

    public static PlayStreamType? ResolvePlayStreamType(string? input)
    {
        return Resolve(input) switch
        {
            VideoInputKind.Video => PlayStreamType.Video,
            VideoInputKind.Bangumi => PlayStreamType.Bangumi,
            VideoInputKind.Cheese => PlayStreamType.Cheese,
            _ => null
        };
    }
}
