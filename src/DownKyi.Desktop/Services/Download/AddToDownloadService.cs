using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

/// <summary>
/// Owns one add-to-download session from media selection through queue admission.
/// </summary>
internal sealed class AddToDownloadService : IAddToDownloadSession
{
    private readonly DownloadTaskAdmissionService _admission;
    private readonly LegacyDownloadAdmissionPresenter _admissionPresenter;
    private readonly DownloadDuplicatePolicy _duplicatePolicy;
    private readonly DownloadMovieMetadataBuilder _metadataBuilder;
    private readonly ISettingsStore _settingsStore;
    private readonly IAppDialogService _dialogService;
    private readonly ILogger<AddToDownloadService> _logger;
    private readonly IInfoService _playbackService;

    public AddToDownloadService(
        PlayStreamType streamType,
        DownloadTaskAdmissionService admission,
        LegacyDownloadAdmissionPresenter admissionPresenter,
        DownloadDuplicatePolicy duplicatePolicy,
        DownloadMovieMetadataBuilder metadataBuilder,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _admissionPresenter = admissionPresenter
            ?? throw new ArgumentNullException(nameof(admissionPresenter));
        _duplicatePolicy = duplicatePolicy ?? throw new ArgumentNullException(nameof(duplicatePolicy));
        _metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(tagProvider);
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        ArgumentNullException.ThrowIfNull(client);

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

    public Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default)
    {
        return _admissionPresenter.EnsureAdmissionAsync(cancellationToken);
    }

    public async Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        bool isAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoInfoView);
        ArgumentNullException.ThrowIfNull(videoSections);

        foreach (var section in videoSections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((isAll || page.IsSelected) && page.PlayUrl != null && page.VideoQuality == null)
                {
                    await RetryMissingVideoQualityAsync(
                        _playbackService,
                        page,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return PreparedDownload.Create(videoInfoView, videoSections);
    }

    public async Task<PreparedDownload?> PrepareAsync(
        IInfoService videoInfoService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoInfoService);
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
                    VideoPages = videoInfoService.GetVideoPages(cancellationToken) ?? new List<VideoPage>()
                }
            ];
        }

        var settings = _settingsStore.Current;
        foreach (var section in videoSections)
        {
            foreach (var item in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.IsSelected = true;
                var playUrl = await videoInfoService
                    .GetVideoStreamAsync(item, cancellationToken)
                    .ConfigureAwait(false);
                VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, item, settings);
                if (item.PlayUrl != null && item.VideoQuality == null)
                {
                    await RetryMissingVideoQualityAsync(
                        videoInfoService,
                        item,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return PreparedDownload.Create(videoInfoView, videoSections);
    }

    public async Task<DownloadAddSelection?> SelectDownloadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = string.Empty;
        var requestedContent = DownloadContentSelection.All;
        var videoSettings = _settingsStore.Current.Video;
        if (videoSettings.IsUseSaveVideoRootPath == AllowStatus.Yes)
        {
            requestedContent = new DownloadContentSelection(
                videoSettings.Content.DownloadAudio,
                videoSettings.Content.DownloadVideo,
                videoSettings.Content.DownloadDanmaku,
                videoSettings.Content.DownloadSubtitle,
                videoSettings.Content.DownloadCover);
            directory = videoSettings.SaveVideoRootPath;
        }
        else
        {
            var result = await _dialogService.ShowAsync(
                new AppDialogRequest(AppDialog.DownloadSettings),
                cancellationToken).ConfigureAwait(true);
            if (result.Outcome == AppDialogOutcome.Accepted)
            {
                directory = result.Parameters.TryGetValue("directory", out var directoryValue)
                    ? directoryValue as string ?? string.Empty
                    : string.Empty;
                requestedContent = ReadDownloadContent(result.Parameters);
            }
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        if (!Directory.Exists(Directory.GetDirectoryRoot(directory)))
        {
            var alert = new AlertService(_dialogService);
            await alert
                .ShowError(DictionaryResource.GetString("DriveNotFound"), cancellationToken)
                .ConfigureAwait(true);
            return null;
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new DownloadAddSelection(directory, requestedContent);
    }

    public async Task<int> AddToDownload(
        DownloadAddSelection selection,
        PreparedDownload preparedDownload,
        bool isAll = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(preparedDownload);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settingsStore.Current;
        var addedCount = 0;
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null;
        foreach (var preparedSection in preparedDownload.Sections)
        {
            foreach (var preparedPage in preparedSection.Pages)
            {
                var page = preparedPage.Page;
                if ((!isAll && !page.IsSelected) || page.PlayUrl == null)
                {
                    continue;
                }

                if (page.VideoQuality == null)
                {
                    continue;
                }

                var videoQuality = page.VideoQuality;
                completedCandidates ??= new Lazy<Task<List<DownloadedItem>>>(() =>
                    _duplicatePolicy.LoadCompletedCandidatesAsync(
                        settings.Basic.RepeatDownloadStrategy,
                        cancellationToken));
                if (await _duplicatePolicy
                    .ShouldSkipAsync(
                        page,
                        videoQuality,
                        settings.Basic.RepeatDownloadStrategy,
                        cancellationToken,
                        completedCandidates)
                    .ConfigureAwait(true))
                {
                    continue;
                }

                var downloadingItem = DownloadTaskDraftFactory.Create(
                    selection.Directory,
                    preparedDownload.Video,
                    preparedSection.Section,
                    preparedDownload.Sections.Count,
                    page,
                    videoQuality,
                    settings,
                    selection.RequestedContent);
                if (settings.Video.Content.GenerateMovieMetadata && selection.RequestedContent.Video)
                {
                    downloadingItem.Metadata = await _metadataBuilder
                        .BuildAsync(preparedDownload.Video, page, cancellationToken)
                        .ConfigureAwait(true);
                }

                try
                {
                    await _admission
                        .AdmitAsync(
                            downloadingItem,
                            settings.Basic.RepeatFileAutoAddNumberSuffix,
                            cancellationToken)
                        .ConfigureAwait(true);
                    addedCount++;
                }
                catch (DownloadRuntimeUnavailableException exception)
                {
                    _logger.LogWarningMessage("Download task admission rejected because runtime is unavailable.", exception);
                    var alert = new AlertService(_dialogService);
                    await alert
                        .ShowError(
                            DictionaryResource.GetString("DownloadRuntimeUnavailable"),
                            cancellationToken)
                        .ConfigureAwait(true);
                    return addedCount;
                }
                catch (IOException exception)
                {
                    _logger.LogWarningMessage("Download task admission failed.", exception);
                    var alert = new AlertService(_dialogService);
                    await alert
                        .ShowError(
                            DictionaryResource.GetString("DirectoryError"),
                            cancellationToken)
                        .ConfigureAwait(true);
                }
            }
        }

        return addedCount;
    }

    private async Task RetryMissingVideoQualityAsync(
        IInfoService videoInfoService,
        VideoPage page,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Current;
        var retry = 0;
        while (page.VideoQuality == null && retry < 5)
        {
            var playUrl = await videoInfoService
                .GetVideoStreamAsync(page, cancellationToken)
                .ConfigureAwait(false);
            VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);
            retry++;
        }
    }

    private static DownloadContentSelection ReadDownloadContent(
        IReadOnlyDictionary<string, object?> parameters)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (value is bool selected)
            {
                values[key] = selected;
            }
        }

        return DownloadContentSelection.FromLegacyMap(values);
    }
}
