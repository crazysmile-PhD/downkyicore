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
        DownloadMovieMetadataBuilder metadataBuilder,
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client,
        IAppDialogService dialogService,
        ILogger<AddToDownloadService> logger)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
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
    public async Task<DownloadSelectionResult> SelectDownloadAsync(
        VideoPage? subtitlePage = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        if (DownloadSettingsDialog.DecodeResult(result) is not { } dialogSelection)
        {
            return DownloadSelectionResult.Canceled;
        }

        var directory = dialogSelection.Directory;
        var requestedContent = dialogSelection.RequestedContent;
        if (string.IsNullOrEmpty(directory))
        {
            return DownloadSelectionResult.Unavailable;
        }
        if (!Directory.Exists(Directory.GetDirectoryRoot(directory)))
        {
            var alert = new AlertService(_dialogService);
            await alert
                .ShowError(DictionaryResource.GetString("DriveNotFound"), cancellationToken)
                .ConfigureAwait(true);
            return DownloadSelectionResult.Unavailable;
        }
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return DownloadSelectionResult.Accepted(
            new DownloadAddSelection(directory, requestedContent));
    }

    public async Task<DownloadAddResult> AddToDownloadBatch(
        DownloadAddSelection selection,
        IReadOnlyList<FinalizedDownload> finalizedDownloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(finalizedDownloads);
        cancellationToken.ThrowIfCancellationRequested();
        if (finalizedDownloads.Count == 1 && finalizedDownloads[0].StopReason != null)
        {
            return DownloadAddResult.FromPlan(finalizedDownloads[0]);
        }

        var settings = _settingsStore.Current;
        var addedCount = 0;
        var duplicateCount = 0;
        var failedCount = 0;
        var skippedCount = finalizedDownloads.Sum(download => download.SkippedCount);
        var pending = CreatePendingDownloads(selection.Directory, finalizedDownloads, settings);
        try
        {
            var review = await _admission
                .ReviewDuplicatesAsync(
                    pending.Select(download => download.Item).ToArray(),
                    cancellationToken)
                .ConfigureAwait(true);
            duplicateCount += review.ActiveDuplicateIds.Count;
            var redownloadCompleted = false;
            if (review.CompletedDuplicateIds.Count > 0)
            {
                var completedNames = pending
                    .Where(download => review.CompletedDuplicateIds.Contains(download.Item.DownloadBase.Id))
                    .Select(download => GetDisplayName(download.Item))
                    .ToArray();
                redownloadCompleted = await DownloadDuplicateBatchDialogContract.ShowAsync(
                    _dialogService,
                    new DownloadDuplicateBatchPrompt(
                        completedNames,
                        review.ActiveDuplicateIds.Count),
                    cancellationToken).ConfigureAwait(true) ==
                    DownloadDuplicateBatchAction.RedownloadCompleted;
                if (!redownloadCompleted)
                {
                    duplicateCount += review.CompletedDuplicateIds.Count;
                }
            }

            foreach (var download in pending)
            {
                if (review.ActiveDuplicateIds.Contains(download.Item.DownloadBase.Id)
                    || review.CompletedDuplicateIds.Contains(download.Item.DownloadBase.Id)
                    && !redownloadCompleted)
                {
                    continue;
                }

                if (settings.Video.Content.GenerateMovieMetadata
                    && download.Item.DownloadBase.NeedDownloadContent.Video)
                {
                    download.Item.Metadata = await _metadataBuilder
                        .BuildAsync(download.Video, download.Page, cancellationToken)
                        .ConfigureAwait(true);
                }
                try
                {
                    var admitted = await _admission
                        .AdmitAsync(
                            download.Item,
                            settings.Basic.RepeatFileAutoAddNumberSuffix,
                            cancellationToken,
                            redownloadCompleted
                            && review.CompletedDuplicateIds.Contains(download.Item.DownloadBase.Id))
                        .ConfigureAwait(true);
                    if (!admitted)
                    {
                        duplicateCount++;
                        continue;
                    }

                    addedCount++;
                }
                catch (DownloadRuntimeUnavailableException exception)
                {
                    failedCount++;
                    _logger.LogWarningMessage("Download task admission rejected because runtime is unavailable.", exception);
                    var alert = new AlertService(_dialogService);
                    await alert
                        .ShowError(
                            DictionaryResource.GetString("DownloadRuntimeUnavailable"),
                            cancellationToken)
                        .ConfigureAwait(true);
                    return DownloadAddResult.FromCounts(
                        addedCount,
                        duplicateCount,
                        failedCount,
                        skippedCount,
                        DownloadAddStopReason.RuntimeUnavailable);
                }
                catch (IOException exception)
                {
                    failedCount++;
                    _logger.LogWarningMessage("Download task admission failed.", exception);
                    var alert = new AlertService(_dialogService);
                    await alert
                        .ShowError(
                            DownloadActivityPresenter.CreateAdmissionError(exception),
                            cancellationToken)
                    .ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && (addedCount > 0 || duplicateCount > 0 || failedCount > 0
                || skippedCount > 0))
        {
            throw new DownloadAddCanceledException(
                DownloadAddResult.CanceledWithProgress(
                    addedCount,
                    duplicateCount,
                    failedCount,
                    skippedCount),
                exception,
                cancellationToken);
        }

        return DownloadAddResult.FromCounts(
            addedCount,
            duplicateCount,
            failedCount,
            skippedCount,
            failedCount > 0 ? DownloadAddStopReason.TaskAdmissionFailed : null);
    }

    private static List<PendingDownload> CreatePendingDownloads(
        string directory,
        IReadOnlyList<FinalizedDownload> finalizedDownloads,
        ApplicationSettings settings)
    {
        var pending = new List<PendingDownload>();
        foreach (var finalizedDownload in finalizedDownloads.Where(download => download.StopReason == null))
        {
            var bangumiMainCoverAssigned = false;
            foreach (var finalizedSection in finalizedDownload.Sections)
            {
                foreach (var finalizedPage in finalizedSection.Pages)
                {
                    var item = DownloadTaskDraftFactory.Create(
                        directory,
                        finalizedDownload.Video,
                        finalizedSection.Section,
                        finalizedDownload.Sections.Count,
                        finalizedPage.Page,
                        finalizedPage.VideoQuality,
                        finalizedPage.AudioQualityFormat,
                        finalizedPage.AvailableMedia,
                        settings,
                        finalizedPage.FinalizedContent);
                    if (DownloadTaskDraftFactory.ResolvePlayStreamType(finalizedDownload.Video.TypeId)
                        == PlayStreamType.Bangumi)
                    {
                        var ownsMainCover = finalizedPage.FinalizedContent.Cover
                                            && !bangumiMainCoverAssigned;
                        if (!ownsMainCover)
                        {
                            item.DownloadBase.CoverUrl = string.Empty;
                        }
                        else if (!string.IsNullOrWhiteSpace(item.DownloadBase.CoverUrl))
                        {
                            bangumiMainCoverAssigned = true;
                        }
                    }

                    pending.Add(new PendingDownload(
                        item,
                        finalizedDownload.Video,
                        finalizedPage.Page));
                }
            }
        }

        return pending;
    }

    private static string GetDisplayName(DownloadingItem item) =>
        string.Equals(item.DownloadBase.MainTitle, item.DownloadBase.Name, StringComparison.Ordinal)
            ? item.DownloadBase.Name
            : $"{item.DownloadBase.MainTitle} - {item.DownloadBase.Name}";

    private sealed record PendingDownload(
        DownloadingItem Item,
        VideoInfoView Video,
        VideoPage Page);

}
