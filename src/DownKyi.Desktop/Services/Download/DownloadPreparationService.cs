using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPreparationService
{
    private readonly IInfoService _playbackService;
    private readonly PlayStreamType _defaultStreamType;
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoTagProvider _tagProvider;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IBilibiliApiClient _client;
    private readonly ILogger _logger;

    public DownloadPreparationService(
        PlayStreamType streamType,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client,
        ILogger logger)
    {
        _defaultStreamType = streamType;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tagProvider = tagProvider ?? throw new ArgumentNullException(nameof(tagProvider));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _playbackService = streamType switch
        {
            PlayStreamType.Video => new VideoInfoService(
                settingsStore,
                tagProvider,
                wbiKeyProvider,
                client),
            PlayStreamType.Bangumi => new BangumiInfoService(settingsStore, client),
            PlayStreamType.Cheese => new CheeseInfoService(settingsStore, client),
            _ => throw new ArgumentOutOfRangeException(nameof(streamType), streamType, null)
        };
    }

    public async Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        DownloadContentSelection requestedContent,
        bool isAll,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);
        ArgumentNullException.ThrowIfNull(requestedContent);
        foreach (var section in videoSections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isAll || page.IsSelected)
                {
                    var playbackService = await ResolvePlaybackServiceAsync(
                        _playbackService,
                        page,
                        cancellationToken).ConfigureAwait(false);
                    if (requestedContent.HasMedia)
                    {
                        await PrepareMediaAsync(
                            playbackService,
                            page,
                            requestedContent,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        return PreparedDownload.Create(videoInfoView, videoSections);
    }

    public async Task<PreparedDownload?> PrepareAsync(
        IInfoService videoInfoService,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoInfoService);
        ArgumentNullException.ThrowIfNull(requestedContent);
        cancellationToken.ThrowIfCancellationRequested();
        var videoInfoView = videoInfoService.GetVideoView(cancellationToken);
        if (videoInfoView == null)
        {
            _logger.LogDebugMessage("VideoInfoView is null.");
            return null;
        }

        var videoSections = videoInfoService.GetVideoSections(true, cancellationToken);
        if (videoSections == null)
        {
            _logger.LogDebugMessage("Video sections do not exist.");
            videoSections =
            [
                new VideoSection
                {
                    Id = 0,
                    Title = "default",
                    IsSelected = true,
                    VideoPages = videoInfoService.GetVideoPages(cancellationToken) ?? []
                }
            ];
        }

        foreach (var section in videoSections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page.IsSelected = true;
                var playbackService = await ResolvePlaybackServiceAsync(
                    videoInfoService,
                    page,
                    cancellationToken).ConfigureAwait(false);
                if (requestedContent.HasMedia)
                {
                    await PrepareMediaAsync(
                        playbackService,
                        page,
                        requestedContent,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return PreparedDownload.Create(videoInfoView, videoSections);
    }

    private async Task<IInfoService> ResolvePlaybackServiceAsync(
        IInfoService defaultService,
        VideoPage page,
        CancellationToken cancellationToken)
    {
        var streamType = page.PlaybackStreamType ?? _defaultStreamType;
        if (streamType == _defaultStreamType)
        {
            return defaultService;
        }

        if (streamType != PlayStreamType.Video)
        {
            throw new InvalidOperationException(
                $"Playback stream type '{streamType}' cannot be prepared by this session.");
        }

        var videoService = await VideoInfoService.CreateAsync(
            page.PlaybackAddress,
            _settingsStore,
            _tagProvider,
            _wbiKeyProvider,
            _client,
            cancellationToken).ConfigureAwait(false);
        var resolvedPages = videoService.GetVideoPages(cancellationToken) ?? [];
        var requestedPage = GetRequestedPage(page.PlaybackAddress);
        var resolvedPage = requestedPage.HasValue
            ? resolvedPages.FirstOrDefault(candidate => candidate.Page == requestedPage.Value)
            : resolvedPages.FirstOrDefault();
        if (resolvedPage == null)
        {
            throw new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Ordinary video metadata did not contain the requested page.");
        }

        page.Avid = resolvedPage.Avid;
        page.Bvid = resolvedPage.Bvid;
        page.Cid = resolvedPage.Cid;
        page.EpisodeId = resolvedPage.EpisodeId;
        page.Page = resolvedPage.Page;
        page.LoadTagsAsync = resolvedPage.LoadTagsAsync;
        if (string.IsNullOrWhiteSpace(page.FirstFrame))
        {
            page.FirstFrame = resolvedPage.FirstFrame;
        }

        return videoService;
    }

    private static int? GetRequestedPage(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && string.Equals(parts[0], "p", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                && page > 0)
            {
                return page;
            }
        }

        return null;
    }

    private async Task PrepareMediaAsync(
        IInfoService videoInfoService,
        VideoPage page,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Current;
        var request = CreateDiscoveryRequest(requestedContent, settings);
        var currentAvailability = page.PlaybackAvailability;
        if (currentAvailability == null
            || request.NeedsApiSupplement(currentAvailability))
        {
            var playUrl = await videoInfoService
                .GetVideoStreamAsync(
                    page,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            VideoPagePlaybackMapper.ApplyPlayUrl(
                playUrl,
                page,
                settings,
                _logger);
        }
    }

    private static PlaybackDiscoveryRequest CreateDiscoveryRequest(
        DownloadContentSelection requestedContent,
        ApplicationSettings settings)
    {
        var videoSettings = settings.Video;
        return new PlaybackDiscoveryRequest(
            videoSettings.Quality,
            videoSettings.AudioQuality,
            requestedContent.Video,
            requestedContent.Audio);
    }
}
