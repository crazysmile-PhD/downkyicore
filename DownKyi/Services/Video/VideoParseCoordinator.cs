using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal sealed class VideoParseCoordinator
{
    private readonly Func<string, CancellationToken, Task<IInfoService?>> _serviceFactory;
    private readonly object _serviceSync = new();
    private IInfoService? _infoService;
    private string? _infoServiceInput;

    public VideoParseCoordinator(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider)
        : this((input, cancellationToken) =>
            CreateInfoServiceAsync(
                input,
                settingsStore,
                tagProvider,
                wbiKeyProvider,
                cancellationToken))
    {
    }

    internal VideoParseCoordinator(Func<string, CancellationToken, IInfoService?> serviceFactory)
        : this((input, cancellationToken) =>
            Task.FromResult(serviceFactory(input, cancellationToken)))
    {
    }

    internal VideoParseCoordinator(Func<string, CancellationToken, Task<IInfoService?>> serviceFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
    }

    private async Task<IInfoService?> AcquireInfoServiceAsync(
        string input,
        bool refresh,
        CancellationToken cancellationToken)
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

        return await _serviceFactory(input, cancellationToken).ConfigureAwait(false);
    }

    public void Reset()
    {
        lock (_serviceSync)
        {
            _infoService = null;
            _infoServiceInput = null;
        }
    }

    public async Task<VideoDetailParseResult> LoadDetailAsync(
        string input,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var service = await AcquireInfoServiceAsync(input, refresh, cancellationToken).ConfigureAwait(false);
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

    public async Task<VideoStreamParseResult?> LoadPageStreamAsync(
        string input,
        VideoPage page,
        bool refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var service = await AcquireInfoServiceAsync(input, refresh, cancellationToken).ConfigureAwait(false);
        if (service == null)
        {
            return null;
        }

        var playUrl = await service.GetVideoStreamAsync(page, cancellationToken).ConfigureAwait(false);
        var result = new VideoStreamParseResult(page, playUrl);
        CommitInfoService(input, service, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<VideoStreamParseResult>> LoadPageStreamsAsync(
        string input,
        IReadOnlyList<VideoPage> pages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var service = await AcquireInfoServiceAsync(input, refresh: false, cancellationToken).ConfigureAwait(false);
        if (service == null)
        {
            return Array.Empty<VideoStreamParseResult>();
        }

        var results = new List<VideoStreamParseResult>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playUrl = await service.GetVideoStreamAsync(page, cancellationToken).ConfigureAwait(false);
            results.Add(new VideoStreamParseResult(page, playUrl));
        }

        CommitInfoService(input, service, cancellationToken);
        return results;
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

    internal static async Task<IInfoService?> CreateInfoServiceAsync(
        string input,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(tagProvider);
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        var kind = VideoInputResolver.Resolve(input);
        if (kind == VideoInputKind.Video)
        {
            return await VideoInfoService.CreateAsync(
                input,
                settingsStore,
                tagProvider,
                wbiKeyProvider,
                cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run<IInfoService?>(() => kind switch
        {
            VideoInputKind.Bangumi => new BangumiInfoService(input, settingsStore, cancellationToken),
            VideoInputKind.Cheese => new CheeseInfoService(input, settingsStore, cancellationToken),
            _ => null
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record VideoDetailParseResult(
    VideoInfoView? VideoInfoView,
    IReadOnlyList<VideoSection> VideoSections)
{
    public static VideoDetailParseResult Empty { get; } = new(null, Array.Empty<VideoSection>());
}

internal sealed record VideoStreamParseResult(VideoPage Page, PlayUrl? PlayUrl);
