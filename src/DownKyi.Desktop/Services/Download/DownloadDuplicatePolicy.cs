using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadDuplicatePolicy
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly IAppDialogService _dialogService;

    public DownloadDuplicatePolicy(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IAppDialogService dialogService)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<bool> ShouldSkipAsync(
        DownloadingItem requestedItem,
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken,
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(requestedItem);
        cancellationToken.ThrowIfCancellationRequested();

        if (ShouldSkipActiveDownload(requestedItem))
        {
            return true;
        }

        var candidates = completedCandidates == null
            ? await LoadCompletedCandidatesAsync(strategy, cancellationToken).ConfigureAwait(true)
            : await completedCandidates.Value.ConfigureAwait(true);
        MergeLiveCompletedCandidates(candidates);
        foreach (var item in candidates)
        {
            if (!IsSameOutput(item, requestedItem))
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
        DownloadingItem requestedItem)
    {
        foreach (var item in _downloadLists.Downloading)
        {
            if (!IsSameOutput(item, requestedItem))
            {
                continue;
            }

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
        DownloadingItem requestedItem)
    {
        var downloadBase = item.DownloadBase;
        var requestedBase = requestedItem.DownloadBase;
        var existingContent = GetRecordedContent(item);
        var requestedContent = requestedBase.NeedDownloadContent;
        return downloadBase.Cid == requestedBase.Cid
               && HasSameOutputPath(item, requestedBase.FilePath)
               && MediaParametersMatch(item, requestedItem, existingContent, requestedContent)
               && IndependentActionsMatch(item, existingContent, requestedContent)
               && CompletedOutputsCover(item, requestedContent);
    }

    private static bool HasSameOutputPath(DownloadBaseItem item, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        var existingPath = item.DownloadBase.FilePath;
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            return string.Equals(
                DownloadOutputPathKey.Create(
                    existingPath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                DownloadOutputPathKey.Create(
                    requestedPath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                StringComparison.Ordinal);
        }

        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return false;
        }

        var requestedKey = DownloadOutputPathKey.Create(
            requestedPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        return history.PublishedArtifacts.Values.Any(path =>
        {
            var artifactKey = DownloadOutputPathKey.Create(
                path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison);
            return artifactKey.StartsWith(requestedKey + ".", StringComparison.Ordinal)
                   || artifactKey.StartsWith(requestedKey + "_", StringComparison.Ordinal);
        });
    }

    private static bool MediaParametersMatch(
        DownloadBaseItem item,
        DownloadingItem requestedItem,
        DownloadContentSelection existingContent,
        DownloadContentSelection requestedContent)
    {
        if (requestedContent.HasMedia
            && (existingContent.Audio != requestedContent.Audio
                || existingContent.Video != requestedContent.Video))
        {
            return false;
        }

        if (requestedContent.Video)
        {
            if (item.Resolution.Id != requestedItem.Resolution.Id
                || item.VideoCodecName != requestedItem.VideoCodecName)
            {
                return false;
            }

            if (existingContent.MediaKind is { } existingKind
                && requestedContent.MediaKind is { } requestedKind
                && existingKind != requestedKind)
            {
                return false;
            }
        }

        if (requestedContent.Audio
            && (!requestedContent.Video || requestedContent.MediaKind != DownloadMediaKind.Durl)
            && item.AudioCodec.Name != requestedItem.AudioCodec.Name)
        {
            return false;
        }

        return true;
    }

    private static bool IndependentActionsMatch(
        DownloadBaseItem item,
        DownloadContentSelection existingContent,
        DownloadContentSelection requestedContent)
    {
        if (requestedContent.Subtitle
            && (!existingContent.Subtitle
                || !SubtitleSelectionMatches(existingContent, requestedContent)))
        {
            return false;
        }

        if (requestedContent.Danmaku
            && (!existingContent.Danmaku
                || !DanmakuSelectionMatches(item, existingContent, requestedContent)))
        {
            return false;
        }

        return !requestedContent.Cover || existingContent.Cover;
    }

    private static bool CompletedOutputsCover(
        DownloadBaseItem item,
        DownloadContentSelection requestedContent)
    {
        var mediaExists = !requestedContent.HasMedia
                          || CompletedOutputExists(item, static key => key == "media");
        var subtitleExists = !requestedContent.Subtitle
                             || CompletedOutputExists(item, static key =>
                                 key.StartsWith("subtitle:", StringComparison.Ordinal));
        var danmakuExists = !requestedContent.Danmaku
                            || CompletedDanmakuOutputsExist(
                                item,
                                requestedContent.DanmakuOutputFormat);
        var coverExists = !requestedContent.Cover
                          || CompletedOutputExists(item, static key =>
                              key is "cover" or "page-cover");
        return mediaExists && subtitleExists && danmakuExists && coverExists;
    }

    private static bool SubtitleSelectionMatches(
        DownloadContentSelection existing,
        DownloadContentSelection requested)
    {
        if (existing.SubtitleTrackSelection != requested.SubtitleTrackSelection
            || existing.DefaultSubtitleTrackId != requested.DefaultSubtitleTrackId)
        {
            return false;
        }

        return existing.SelectedSubtitleTrackIds is not { } existingIds
               || requested.SelectedSubtitleTrackIds is { } requestedIds
               && existingIds.ToHashSet().SetEquals(requestedIds);
    }

    private static bool DanmakuSelectionMatches(
        DownloadBaseItem item,
        DownloadContentSelection existing,
        DownloadContentSelection requested)
    {
        var existingFormat = existing.DanmakuOutputFormat;
        if (existingFormat == null && item is DownloadedItem { HistoryRecord: { } history })
        {
            var hasAss = history.PublishedArtifacts.ContainsKey(
                DownloadArtifactWriter.DanmakuAssTransferKey);
            var hasXml = history.PublishedArtifacts.ContainsKey(
                DownloadArtifactWriter.DanmakuXmlTransferKey);
            existingFormat = (hasAss, hasXml) switch
            {
                (true, true) => DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml,
                (true, false) => DownloadDanmakuOutputFormat.Ass,
                (false, true) => DownloadDanmakuOutputFormat.Xml,
                _ => null
            };
        }

        return existingFormat == requested.DanmakuOutputFormat;
    }

    private static bool CompletedDanmakuOutputsExist(
        DownloadBaseItem item,
        DownloadDanmakuOutputFormat? requestedFormat)
    {
        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return true;
        }

        var requireAss = requestedFormat?.HasFlag(DownloadDanmakuOutputFormat.Ass) == true;
        var requireXml = requestedFormat?.HasFlag(DownloadDanmakuOutputFormat.Xml) == true;
        return (!requireAss || HasUsableArtifact(
                   history,
                   DownloadArtifactWriter.DanmakuAssTransferKey))
               && (!requireXml || HasUsableArtifact(
                   history,
                   DownloadArtifactWriter.DanmakuXmlTransferKey));
    }

    private static bool CompletedOutputExists(
        DownloadBaseItem item,
        Func<string, bool> keyPredicate)
    {
        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return true;
        }

        return history.PublishedArtifacts
            .Where(artifact => keyPredicate(artifact.Key))
            .Any(artifact => DownloadFileIntegrity.Check(artifact.Value).IsUsable);
    }

    private static bool HasUsableArtifact(
        DownKyi.Application.Downloads.DownloadHistoryRecord history,
        string key) =>
        history.PublishedArtifacts.TryGetValue(key, out var path)
        && DownloadFileIntegrity.Check(path).IsUsable;

    private static DownloadContentSelection GetRecordedContent(DownloadBaseItem item)
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
