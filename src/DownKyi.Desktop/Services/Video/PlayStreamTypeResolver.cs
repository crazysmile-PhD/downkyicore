using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.VideoStream;

namespace DownKyi.Services.Video;

internal static class PlayStreamTypeResolver
{
    public static PlayStreamType? ResolvePlayStreamType(string? input)
    {
        return VideoInputResolver.Resolve(input) switch
        {
            VideoInputKind.Video => PlayStreamType.Video,
            VideoInputKind.Bangumi => PlayStreamType.Bangumi,
            VideoInputKind.Cheese => PlayStreamType.Cheese,
            _ => null
        };
    }
}
