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
    private enum MediaOutputIdentity
    {
        Unknown,
        SidecarOnly,
        Video,
        Audio
    }

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
            if (!IsSameVideo(item, page, videoQuality, requestedContent))
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
            if (!IsSameVideo(item, page, videoQuality, requestedContent))
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

    private static bool IsSameVideo(
        DownloadBaseItem item,
        VideoPage page,
        VideoQuality? videoQuality,
        DownloadContentSelection requestedContent)
    {
        var existingOutput = GetMediaOutput(item);
        if (existingOutput != MediaOutputIdentity.Unknown
            && existingOutput != GetMediaOutput(requestedContent))
        {
            return false;
        }

        var downloadBase = item.DownloadBase;
        var isSameVideo = downloadBase.Cid == page.Cid;
        if (videoQuality == null)
        {
            return isSameVideo && item.AudioCodec.Name == page.AudioQualityFormat;
        }

        isSameVideo = isSameVideo
                      && item.Resolution.Id == videoQuality.Quality
                      && item.VideoCodecName == videoQuality.SelectedVideoCodec;
        if (!videoQuality.IsDurl)
        {
            isSameVideo = isSameVideo && item.AudioCodec.Name == page.AudioQualityFormat;
        }

        return isSameVideo;
    }

    private static MediaOutputIdentity GetMediaOutput(DownloadBaseItem item)
    {
        if (item is DownloadedItem { HistoryRecord: { } history })
        {
            if (history.PublishedArtifacts.TryGetValue("media", out var mediaPath))
            {
                var extension = Path.GetExtension(mediaPath);
                if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    return MediaOutputIdentity.Audio;
                }

                return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? MediaOutputIdentity.Video
                    : MediaOutputIdentity.Unknown;
            }

            // Current completed tasks publish every output. Legacy history can
            // have an empty artifact map, which does not identify its media.
            return history.PublishedArtifacts.Count > 0
                ? MediaOutputIdentity.SidecarOnly
                : MediaOutputIdentity.Unknown;
        }

        return GetMediaOutput(item.DownloadBase.NeedDownloadContent);
    }

    private static MediaOutputIdentity GetMediaOutput(DownloadContentSelection content)
    {
        if (content.Video)
        {
            // Duplicate admission has always treated video-only and muxed video
            // as one video-bearing output. Preserve that contract here.
            return MediaOutputIdentity.Video;
        }

        return content.Audio
            ? MediaOutputIdentity.Audio
            : MediaOutputIdentity.SidecarOnly;
    }
}
