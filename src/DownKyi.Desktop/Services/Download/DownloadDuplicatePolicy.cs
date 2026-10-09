using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadDuplicatePolicy
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly IUserNotificationService _notificationService;
    private readonly IAppDialogService _dialogService;

    public DownloadDuplicatePolicy(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IUserNotificationService notificationService,
        IAppDialogService dialogService)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<bool> ShouldSkipAsync(
        VideoPage page,
        VideoQuality? videoQuality,
        DownloadContentSelection requestedContent,
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken,
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(requestedContent);
        cancellationToken.ThrowIfCancellationRequested();

        if (ShouldSkipActiveDownload(page, videoQuality, requestedContent))
        {
            return true;
        }

        var candidates = completedCandidates == null
            ? await LoadCompletedCandidatesAsync(strategy, cancellationToken).ConfigureAwait(true)
            : await completedCandidates.Value.ConfigureAwait(true);
        MergeLiveCompletedCandidates(candidates);
        foreach (var item in candidates)
        {
            if (!IsSameOutput(item, page, videoQuality, requestedContent))
            {
                continue;
            }

            var shouldSkip = strategy switch
            {
                RepeatDownloadStrategy.Ask => await ResolveAskAsync(item, cancellationToken)
                    .ConfigureAwait(true),
                RepeatDownloadStrategy.ReDownload => false,
                RepeatDownloadStrategy.JumpOver => true,
                _ => true
            };
            if (!shouldSkip)
            {
                candidates.Remove(item);
            }

            return shouldSkip;
        }

        return false;
    }

    public async Task<List<DownloadedItem>> LoadCompletedCandidatesAsync(
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (strategy == RepeatDownloadStrategy.ReDownload)
        {
            return [];
        }

        var downloadedItems = await _projectionStore
            .GetDownloadedAsync(cancellationToken)
            .ConfigureAwait(true);
        return new List<DownloadedItem>(downloadedItems);
    }

    private bool ShouldSkipActiveDownload(
        VideoPage page,
        VideoQuality? videoQuality,
        DownloadContentSelection requestedContent)
    {
        foreach (var item in _downloadLists.Downloading)
        {
            if (!IsSameOutput(item, page, videoQuality, requestedContent))
            {
                continue;
            }

            _notificationService.Show(DictionaryResource.GetString("TipAlreadyToAddDownloading"));
            return true;
        }

        return false;
    }

    private void MergeLiveCompletedCandidates(List<DownloadedItem> completedCandidates)
    {
        var candidateIds = completedCandidates
            .Select(GetTaskId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in _downloadLists.Downloaded)
        {
            if (candidateIds.Add(GetTaskId(item)))
            {
                completedCandidates.Add(item);
            }
        }
    }

    private static string GetTaskId(DownloadedItem item) =>
        item.HistoryRecord?.Id.Value ?? item.DownloadBase.Id;

    private async Task<bool> ResolveAskAsync(
        DownloadedItem item,
        CancellationToken cancellationToken)
    {
        var result = await _dialogService.ShowAsync(
            new AppDialogRequest(
                AppDialog.AlreadyDownloaded,
                new Dictionary<string, object?>
                {
                    ["message"] = $"{item.Name}已下载，是否重新下载"
                }),
            cancellationToken).ConfigureAwait(true);
        if (result.Outcome != AppDialogOutcome.Accepted)
        {
            return true;
        }

        await _projectionStore
            .RemoveDownloadedAsync(item, cancellationToken)
            .ConfigureAwait(true);
        _downloadLists.RemoveDownloaded(item);
        return false;
    }

    private static bool IsSameOutput(
        DownloadBaseItem item,
        VideoPage page,
        VideoQuality? videoQuality,
        DownloadContentSelection requestedContent)
    {
        var downloadBase = item.DownloadBase;
        if (downloadBase.Cid != page.Cid)
        {
            return false;
        }

        var existingContent = GetRecordedMediaContent(item);
        if (existingContent.Audio != requestedContent.Audio
            || existingContent.Video != requestedContent.Video)
        {
            return false;
        }

        if (requestedContent.Video)
        {
            if (videoQuality == null
                || item.Resolution.Id != videoQuality.Quality
                || item.VideoCodecName != videoQuality.SelectedVideoCodec)
            {
                return false;
            }

            var requestedKind = videoQuality.IsDurl
                ? DownloadMediaKind.Durl
                : DownloadMediaKind.Dash;
            if (existingContent.MediaKind is { } kind && kind != requestedKind)
            {
                return false;
            }
        }

        if (requestedContent.Audio
            && (!requestedContent.Video || videoQuality?.IsDurl != true)
            && item.AudioCodec.Name != page.AudioQualityFormat)
        {
            return false;
        }

        return requestedContent.Audio || requestedContent.Video
            || (existingContent.Danmaku == requestedContent.Danmaku
                && existingContent.Subtitle == requestedContent.Subtitle
                && existingContent.Cover == requestedContent.Cover);
    }

    private static DownloadContentSelection GetRecordedMediaContent(DownloadBaseItem item)
    {
        var content = item.DownloadBase.NeedDownloadContent;
        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return content;
        }

        if (history.RequestedContent is { } finalizedContent)
        {
            return finalizedContent;
        }

        if (!history.PublishedArtifacts.TryGetValue("media", out var mediaPath))
        {
            return history.PublishedArtifacts.Count == 0
                ? content
                : content with { Audio = false, Video = false, MediaKind = DownloadMediaKind.None };
        }

        var extension = Path.GetExtension(mediaPath);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            return content with { Audio = true, Video = false, MediaKind = DownloadMediaKind.Dash };
        }

        return content;
    }
}
