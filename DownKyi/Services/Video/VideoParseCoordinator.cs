using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal sealed class VideoParseCoordinator
{
    private readonly Func<string, CancellationToken, IInfoService?> _serviceFactory;
    private readonly object _serviceSync = new();
    private IInfoService? _infoService;
    private string? _infoServiceInput;

    public VideoParseCoordinator(ISettingsStore settingsStore, IVideoTagProvider tagProvider)
        : this((input, cancellationToken) =>
            CreateInfoService(input, settingsStore, tagProvider, cancellationToken))
    {
    }

    internal VideoParseCoordinator(Func<string, CancellationToken, IInfoService?> serviceFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
    }

    private IInfoService? AcquireInfoService(string input, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!refresh)
        {
            lock (_serviceSync)
            {
                if (_infoService != null && string.Equals(_infoServiceInput, input, StringComparison.Ordinal))
                {
                    return _infoService;
                }
            }
        }

        return _serviceFactory(input, cancellationToken);
    }

    public void Reset()
    {
        lock (_serviceSync)
        {
            _infoService = null;
            _infoServiceInput = null;
        }
    }

    public Task<VideoDetailParseResult> LoadDetailAsync(
        string input,
        bool refresh,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => LoadDetail(input, refresh, cancellationToken), cancellationToken);
    }

    private VideoDetailParseResult LoadDetail(
        string input,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var service = AcquireInfoService(input, refresh, cancellationToken);
        if (service == null)
        {
            return VideoDetailParseResult.Empty;
        }

        var videoInfoView = service.GetVideoView(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (videoInfoView == null)
        {
            return VideoDetailParseResult.Empty;
        }

        var videoSections = service.GetVideoSections(false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (videoSections != null)
        {
            var result = new VideoDetailParseResult(videoInfoView, videoSections.ToArray());
            CommitInfoService(input, service, cancellationToken);
            return result;
        }

        var pages = service.GetVideoPages(cancellationToken) ?? new List<VideoPage>();
        cancellationToken.ThrowIfCancellationRequested();
        VideoSection[] defaultSections =
        [
            new VideoSection
            {
                Id = 0,
                Title = "default",
                IsSelected = true,
                VideoPages = pages
            }
        ];
        var defaultResult = new VideoDetailParseResult(videoInfoView, defaultSections);
        CommitInfoService(input, service, cancellationToken);
        return defaultResult;
    }

    public Task<VideoStreamParseResult?> LoadPageStreamAsync(
        string input,
        VideoPage page,
        bool refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Task.Run(() =>
        {
            var service = AcquireInfoService(input, refresh, cancellationToken);
            if (service == null)
            {
                return null;
            }

            var result = new VideoStreamParseResult(page, service.GetVideoStream(page, cancellationToken));
            CommitInfoService(input, service, cancellationToken);
            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VideoStreamParseResult>> LoadPageStreamsAsync(
        string input,
        IReadOnlyList<VideoPage> pages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Task.Run(() =>
        {
            var service = AcquireInfoService(input, refresh: false, cancellationToken);
            if (service == null)
            {
                return (IReadOnlyList<VideoStreamParseResult>)Array.Empty<VideoStreamParseResult>();
            }

            var results = new List<VideoStreamParseResult>(pages.Count);
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var playUrl = service.GetVideoStream(page, cancellationToken);
                results.Add(new VideoStreamParseResult(page, playUrl));
            }

            CommitInfoService(input, service, cancellationToken);
            return results;
        }, cancellationToken);
    }

    private void CommitInfoService(
        string input,
        IInfoService infoService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_serviceSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _infoService = infoService;
            _infoServiceInput = input;
        }
    }

    internal static IInfoService? CreateInfoService(
        string input,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(tagProvider);
        return VideoInputResolver.Resolve(input) switch
        {
            VideoInputKind.Video => new VideoInfoService(input, settingsStore, tagProvider, cancellationToken),
            VideoInputKind.Bangumi => new BangumiInfoService(input, settingsStore, cancellationToken),
            VideoInputKind.Cheese => new CheeseInfoService(input, settingsStore, cancellationToken),
            _ => null
        };
    }
}

internal sealed record VideoDetailParseResult(
    VideoInfoView? VideoInfoView,
    IReadOnlyList<VideoSection> VideoSections)
{
    public static VideoDetailParseResult Empty { get; } = new(null, Array.Empty<VideoSection>());
}

internal sealed record VideoStreamParseResult(VideoPage Page, PlayUrl? PlayUrl);
