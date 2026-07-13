using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Media;
using DownKyi.ViewModels.PageViewModels;

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

    public Task ExecuteAsync(
        string input,
        bool refresh,
        Action<IInfoService, CancellationToken> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(() =>
        {
            var service = GetInfoService(input, refresh, cancellationToken);
            if (service != null)
            {
                action(service, cancellationToken);
            }
        }, cancellationToken);
    }

    public Task ExecutePageAsync(
        string input,
        VideoPage page,
        bool refresh,
        Action<IInfoService, VideoPage, CancellationToken> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(() =>
        {
            var service = GetInfoService(input, refresh, cancellationToken);
            if (service != null)
            {
                action(service, page, cancellationToken);
            }
        }, cancellationToken);
    }

    public Task ExecutePagesAsync(
        string input,
        IReadOnlyList<VideoPage> pages,
        Action<IInfoService, VideoPage, CancellationToken> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(() =>
        {
            var service = GetInfoService(input, refresh: false, cancellationToken);
            if (service == null)
            {
                return;
            }

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action(service, page, cancellationToken);
            }
        }, cancellationToken);
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
