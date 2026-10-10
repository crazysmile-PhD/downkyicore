using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi;
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
    private readonly DownloadPreparationService _preparationService;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IBilibiliApiClient _client;
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
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _preparationService = new DownloadPreparationService(
            streamType,
            settingsStore,
            tagProvider,
            wbiKeyProvider,
            client,
            logger);
    }
    public Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken = default)
    {
        return _admissionPresenter.EnsureAdmissionAsync(cancellationToken);
    }
    public Task<PreparedDownload> PrepareAsync(
        VideoInfoView videoInfoView,
        IList<VideoSection> videoSections,
        DownloadContentSelection requestedContent,
        bool isAll,
        CancellationToken cancellationToken = default)
    {
        return _preparationService.PrepareAsync(
            videoInfoView,
            videoSections,
            requestedContent,
            isAll,
            cancellationToken);
    }
    public Task<PreparedDownload?> PrepareAsync(
        IInfoService videoInfoService,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken = default)
    {
        return _preparationService.PrepareAsync(
            videoInfoService,
            requestedContent,
            cancellationToken);
    }
    public async Task<DownloadAddSelection?> SelectDownloadAsync(
        VideoPage? subtitlePage = null,
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
            var subtitleDiscovery = new DownloadSettingsDialog.SubtitleTrackDiscovery(
                DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NotAttempted,
                []);
            if (subtitlePage != null)
            {
                try
                {
                    var player = await WbiRequestExecutor.ExecuteAsync(
                        _wbiKeyProvider,
                        (keys, unixTimeSeconds) => _client.PlayerV2Async(
                            keys, unixTimeSeconds, subtitlePage.Avid, subtitlePage.Bvid,
                            subtitlePage.Cid, cancellationToken),
                        TimeProvider.System,
                        cancellationToken).ConfigureAwait(false);
                    var subtitleTracks = player?.Subtitle?.Subtitles?.Select(track =>
                        new DownloadSettingsDialog.SubtitleTrack(
                            track.Id, track.Lan, track.LanDoc, track.Type, track.SubtitleAddress))
                        .ToArray() ?? [];
                    subtitleDiscovery = new DownloadSettingsDialog.SubtitleTrackDiscovery(
                        subtitleTracks.Length > 0
                            ? DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Available
                            : DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NoResource,
                        subtitleTracks);
                }
                catch (Exception exception) when (exception is HttpRequestException or BilibiliApiResponseException
                                                  || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarningMessage("Subtitle tracks could not be loaded; opening download settings without them.", exception);
                    subtitleDiscovery = new DownloadSettingsDialog.SubtitleTrackDiscovery(
                        DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Failed,
                        []);
                }
            }
            var result = await _dialogService.ShowAsync(
                DownloadSettingsDialog.CreateRequest(subtitleDiscovery),
                cancellationToken).ConfigureAwait(true);
            if (DownloadSettingsDialog.DecodeResult(result) is { } dialogSelection)
            {
                directory = dialogSelection.Directory;
                requestedContent = dialogSelection.RequestedContent;
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
    public async Task<DownloadAddResult> AddToDownload(
        string directory,
        FinalizedDownload finalizedDownload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(finalizedDownload);
        cancellationToken.ThrowIfCancellationRequested();
        if (finalizedDownload.StopReason != null)
        {
            return DownloadAddResult.FromPlan(finalizedDownload);
        }

        var settings = _settingsStore.Current;
        var addedCount = 0;
        var duplicateCount = 0;
        var failedCount = 0;
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null;
        Lazy<Task<List<DownloadingItem>>>? activeCandidates = null;
        foreach (var finalizedSection in finalizedDownload.Sections)
        {
            foreach (var finalizedPage in finalizedSection.Pages)
            {
                var page = finalizedPage.Page;
                var downloadingItem = DownloadTaskDraftFactory.Create(
                    directory,
                    finalizedDownload.Video,
                    finalizedSection.Section,
                    finalizedDownload.Sections.Count,
                    page,
                    finalizedPage.VideoQuality,
                    settings,
                    finalizedPage.FinalizedContent);
                completedCandidates ??= new Lazy<Task<List<DownloadedItem>>>(() =>
                    _duplicatePolicy.LoadCompletedCandidatesAsync(
                        cancellationToken));
                activeCandidates ??= new Lazy<Task<List<DownloadingItem>>>(() =>
                    _duplicatePolicy.LoadActiveCandidatesAsync(cancellationToken));
                var duplicateResolution = await _duplicatePolicy
                    .ResolveAsync(
                        downloadingItem,
                        settings.Basic.RepeatDownloadStrategy,
                        cancellationToken,
                        completedCandidates,
                        activeCandidates)
                    .ConfigureAwait(true);
                if (duplicateResolution.IsFullyCovered)
                {
                    duplicateCount++;
                    continue;
                }
                downloadingItem.DownloadBase.NeedDownloadContent =
                    duplicateResolution.RemainingContent;
                if (settings.Video.Content.GenerateMovieMetadata
                    && duplicateResolution.RemainingContent.Video)
                {
                    downloadingItem.Metadata = await _metadataBuilder
                        .BuildAsync(finalizedDownload.Video, page, cancellationToken)
                        .ConfigureAwait(true);
                }
                try
                {
                    await _admission
                        .AdmitAsync(
                            downloadingItem,
                            settings.Basic.RepeatFileAutoAddNumberSuffix,
                             cancellationToken,
                             duplicateResolution.AllowExistingBasePath)
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
                    failedCount++;
                    return CreateResult(
                        addedCount,
                        duplicateCount,
                        failedCount,
                        finalizedDownload.SkippedCount);
                }
                catch (IOException exception)
                {
                    failedCount++;
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

        return CreateResult(
            addedCount,
            duplicateCount,
            failedCount,
            finalizedDownload.SkippedCount);
    }

    private static DownloadAddResult CreateResult(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount)
    {
        return new DownloadAddResult(
            addedCount,
            duplicateCount,
            failedCount,
            skippedCount);
    }

}
