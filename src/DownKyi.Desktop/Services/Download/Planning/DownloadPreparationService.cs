using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPreparationService
{
    private readonly IInfoService _playbackService;
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

    public Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        DownloadContentSelection requestedContent,
        bool isAll,
        CancellationToken cancellationToken) => PreparePagesAsync(
            _playbackService, videoInfoView, videoSections, requestedContent, isAll, cancellationToken);

    public async Task<PreparedDownload?> PrepareAsync(
        BilibiliMediaInput input,
        IInfoService videoInfoService,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(videoInfoService);
        ArgumentNullException.ThrowIfNull(requestedContent);
        cancellationToken.ThrowIfCancellationRequested();
        var videoInfoView = videoInfoService.GetVideoView(cancellationToken);
        if (videoInfoView == null)
        {
            _logger.LogDebugMessage("VideoInfoView is null.");
            return null;
        }

        var episodeId = input switch
        {
            BilibiliBangumiEpisodeInput episode => (long?)episode.EpisodeId,
            BilibiliCheeseEpisodeInput episode => episode.EpisodeId,
            _ => null
        };
        var videoSections = videoInfoService.GetVideoSections(episodeId == null, cancellationToken);
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

        if (episodeId is { } selectedEpisodeId)
            videoSections = SelectEpisode(videoSections, selectedEpisodeId, cancellationToken);

        foreach (var section in videoSections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page.IsSelected = true;
            }
        }

        return await PreparePagesAsync(
            videoInfoService, videoInfoView, videoSections, requestedContent, isAll: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PreparedDownload> PreparePagesAsync(
        IInfoService sourceService,
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        DownloadContentSelection requestedContent,
        bool isAll,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);
        ArgumentNullException.ThrowIfNull(requestedContent);
        var resolvedVideos = new Dictionary<string, ResolvedVideo>(StringComparer.Ordinal);
        var preparedSections = new List<PreparedDownloadSection>(videoSections.Count);
        foreach (var section in videoSections)
        {
            var preparedPages = new List<PreparedDownloadPage>();
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isAll && !page.IsSelected)
                {
                    continue;
                }

                var video = videoInfoView;
                if (!string.IsNullOrWhiteSpace(videoInfoView.Bvid)
                    && !string.Equals(videoInfoView.Bvid, page.Bvid, StringComparison.Ordinal))
                {
                    var resolved = await ResolveCollectionVideoAsync(
                        page.Bvid, resolvedVideos, cancellationToken).ConfigureAwait(false);
                    if (!resolved.Pages.TryGetValue(page.Cid, out var resolvedPage))
                    {
                        throw new BilibiliApiResponseException(
                            nameof(VideoInfo.VideoViewInfoAsync),
                            "Collection video metadata did not contain the selected page.");
                    }

                    page.Avid = resolvedPage.Avid;
                    page.Page = resolvedPage.Page;
                    page.Name = resolvedPage.Name;
                    page.Owner = resolvedPage.Owner;
                    page.PublishTime = resolvedPage.PublishTime;
                    page.OriginalPublishTime = resolvedPage.OriginalPublishTime;
                    page.LoadTagsAsync = resolvedPage.LoadTagsAsync;
                    if (!string.IsNullOrWhiteSpace(resolvedPage.FirstFrame))
                    {
                        page.FirstFrame = resolvedPage.FirstFrame;
                    }

                    video = resolved.Video;
                }

                var playbackService = await ResolvePlaybackServiceAsync(
                    sourceService, page, cancellationToken).ConfigureAwait(false);
                if (requestedContent.HasMedia)
                {
                    await PrepareMediaAsync(
                        playbackService, page, requestedContent, cancellationToken).ConfigureAwait(false);
                }

                preparedPages.Add(PreparedDownloadPage.Create(page, video));
            }

            preparedSections.Add(new PreparedDownloadSection(section, preparedPages));
        }

        return new PreparedDownload(preparedSections);
    }

    private static IList<VideoSection> SelectEpisode(
        IList<VideoSection> sections, long episodeId, CancellationToken cancellationToken)
    {
        foreach (var section in sections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.SourceEpisodeId != episodeId) continue;
                section.VideoPages = [page];
                return [section];
            }
        }
        throw new BilibiliApiResponseException(nameof(PrepareAsync),
            "The metadata did not contain the requested episode.");
    }

    private async Task<ResolvedVideo> ResolveCollectionVideoAsync(
        string bvid,
        Dictionary<string, ResolvedVideo> resolvedVideos,
        CancellationToken cancellationToken)
    {
        if (resolvedVideos.TryGetValue(bvid, out var resolved))
        {
            return resolved;
        }

        var service = await VideoInfoService.CreateAsync(
            new BilibiliBvidInput(bvid), _settingsStore, _tagProvider, _wbiKeyProvider, _client, cancellationToken)
            .ConfigureAwait(false);
        var video = service.GetVideoView(cancellationToken)
            ?? throw new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Collection video metadata was unavailable.");
        var pages = service.GetVideoPages(cancellationToken);
        if (pages == null || !string.Equals(video.Bvid, bvid, StringComparison.Ordinal))
        {
            throw new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "Collection video metadata did not match the requested video.");
        }

        resolved = new ResolvedVideo(video, pages.ToDictionary(page => page.Cid));
        resolvedVideos.Add(bvid, resolved);
        return resolved;
    }

    private async Task<IInfoService> ResolvePlaybackServiceAsync(
        IInfoService defaultService,
        VideoPage page,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(page.PlaybackAddress))
        {
            return defaultService;
        }

        if (BilibiliInputParser.Parse(page.PlaybackAddress) is not BilibiliMediaInput input)
        {
            throw new BilibiliApiResponseException(
                nameof(VideoInfo.VideoViewInfoAsync),
                "The playback address was not a supported Bilibili video input.");
        }

        var videoService = await VideoInfoService.CreateAsync(
            input,
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

    private sealed record ResolvedVideo(VideoInfoView Video, IReadOnlyDictionary<long, VideoPage> Pages);
}
