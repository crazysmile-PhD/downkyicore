using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Presentation;

namespace DownKyi.Services.Video;

internal sealed class VideoParseCoordinator
{
    private readonly Func<BilibiliMediaInput, CancellationToken, Task<IInfoService?>> _serviceFactory;

    public VideoParseCoordinator(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client)
        : this((input, cancellationToken) =>
            CreateInfoServiceAsync(
                input,
                settingsStore,
                tagProvider,
                wbiKeyProvider,
                client,
                cancellationToken))
    {
    }

    internal VideoParseCoordinator(Func<BilibiliMediaInput, CancellationToken, IInfoService?> serviceFactory)
        : this((input, cancellationToken) =>
            Task.FromResult(serviceFactory(input, cancellationToken)))
    {
    }

    internal VideoParseCoordinator(Func<BilibiliMediaInput, CancellationToken, Task<IInfoService?>> serviceFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
    }

    public async Task<VideoDetailParseResult> LoadDetailAsync(
        BilibiliMediaInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = await _serviceFactory(input, cancellationToken).ConfigureAwait(false);
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
            return VideoDetailParseResult.From(videoInfoView, videoSections);
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
        return VideoDetailParseResult.From(videoInfoView, defaultSections);
    }

    internal static async Task<IInfoService?> CreateInfoServiceAsync(
        BilibiliMediaInput input,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(tagProvider);
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        ArgumentNullException.ThrowIfNull(client);
        if (input is BilibiliBvidInput or BilibiliAvidInput)
        {
            return await VideoInfoService.CreateAsync(
                input,
                settingsStore,
                tagProvider,
                wbiKeyProvider,
                client,
                cancellationToken).ConfigureAwait(false);
        }

        return input switch
        {
            BilibiliBangumiSeasonInput or BilibiliBangumiEpisodeInput or BilibiliBangumiMediaInput
                => await BangumiInfoService.CreateAsync(
                input,
                settingsStore,
                client,
                cancellationToken).ConfigureAwait(false),
            BilibiliCheeseSeasonInput or BilibiliCheeseEpisodeInput
                => await CheeseInfoService.CreateAsync(
                input,
                settingsStore,
                client,
                cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }
}

internal enum VideoDetailParseOutcome
{
    NoContent,
    Ready
}

internal sealed record VideoDetailParseResult
{
    private VideoDetailParseResult(
        VideoDetailParseOutcome outcome,
        VideoInfoView? videoInfoView,
        IReadOnlyList<VideoSection> videoSections)
    {
        Outcome = outcome;
        VideoInfoView = videoInfoView;
        VideoSections = videoSections;
    }

    public VideoDetailParseOutcome Outcome { get; }

    public VideoInfoView? VideoInfoView { get; }

    public IReadOnlyList<VideoSection> VideoSections { get; }

    public static VideoDetailParseResult Empty { get; } = new(
        VideoDetailParseOutcome.NoContent,
        null,
        Array.Empty<VideoSection>());

    public static VideoDetailParseResult From(
        VideoInfoView videoInfoView,
        IEnumerable<VideoSection> videoSections)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);
        var sections = videoSections.ToArray();
        return sections.Any(section => section.VideoPages.Count > 0)
            ? new VideoDetailParseResult(VideoDetailParseOutcome.Ready, videoInfoView, sections)
            : Empty;
    }
}
