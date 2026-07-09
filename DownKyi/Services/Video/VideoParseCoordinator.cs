using System.Threading;

namespace DownKyi.Services.Video;

internal sealed class VideoParseCoordinator
{
    private IInfoService? _infoService;

    public IInfoService? GetInfoService(string input, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_infoService != null && !refresh)
        {
            return _infoService;
        }

        _infoService = CreateInfoService(input, cancellationToken);
        return _infoService;
    }

    public void Reset()
    {
        _infoService = null;
    }

    internal static IInfoService? CreateInfoService(string input, CancellationToken cancellationToken)
    {
        return VideoInputResolver.Resolve(input) switch
        {
            VideoInputKind.Video => new VideoInfoService(input, cancellationToken),
            VideoInputKind.Bangumi => new BangumiInfoService(input, cancellationToken),
            VideoInputKind.Cheese => new CheeseInfoService(input, cancellationToken),
            _ => null
        };
    }
}
