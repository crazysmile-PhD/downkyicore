using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.VideoStream;

namespace DownKyi.Services.Video;

internal static class VideoInputResolver
{
    public static VideoInputKind Resolve(string? input)
    {
        return DownKyi.Application.Media.VideoInputResolver.Resolve(input);
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
