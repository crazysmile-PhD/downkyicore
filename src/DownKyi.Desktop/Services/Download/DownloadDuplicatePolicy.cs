using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
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

    public async Task<DownloadDuplicateResolution> ResolveAsync(
        DownloadingItem requestedItem,
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken,
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null,
        Lazy<Task<List<DownloadingItem>>>? activeCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(requestedItem);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedContent = requestedItem.DownloadBase.NeedDownloadContent;
        var remainingContent = requestedContent;
        var hasCoveredContent = false;
        var active = activeCandidates == null
            ? await LoadActiveCandidatesAsync(cancellationToken).ConfigureAwait(true)
            : await activeCandidates.Value.ConfigureAwait(true);
        MergeLiveActiveCandidates(active);
        foreach (var item in active)
        {
            var reduced = DownloadActionCoverage.RemoveCoveredActions(
                item,
                requestedItem,
                remainingContent,
                requireUsableArtifacts: false);
            hasCoveredContent |= reduced != remainingContent;
            remainingContent = reduced;
            if (!remainingContent.HasAnyRequestedAction)
            {
                return DownloadDuplicateResolution.FullyCovered(requestedContent);
            }
        }

        if (strategy == RepeatDownloadStrategy.ReDownload)
        {
            return new DownloadDuplicateResolution(
                requestedContent,
                AllowExistingBasePath: false);
        }

        var candidates = completedCandidates == null
            ? await LoadCompletedCandidatesAsync(strategy, cancellationToken).ConfigureAwait(true)
            : await completedCandidates.Value.ConfigureAwait(true);
        MergeLiveCompletedCandidates(candidates);
        foreach (var item in candidates.ToArray())
        {
            var reduced = DownloadActionCoverage.RemoveCoveredActions(
                item,
                requestedItem,
                remainingContent,
                requireUsableArtifacts: true);
            if (reduced == remainingContent)
            {
                continue;
            }

            if (strategy == RepeatDownloadStrategy.Ask
                && await ShouldRedownloadAsync(item, cancellationToken).ConfigureAwait(true))
            {
                candidates.Remove(item);
                continue;
            }

            hasCoveredContent = true;
            remainingContent = reduced;
            if (!remainingContent.HasAnyRequestedAction)
            {
                return DownloadDuplicateResolution.FullyCovered(requestedContent);
            }
        }

        return new DownloadDuplicateResolution(
            remainingContent,
            AllowExistingBasePath: hasCoveredContent);
    }

    internal async Task<bool> ShouldSkipAsync(
        DownloadingItem requestedItem,
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken,
        Lazy<Task<List<DownloadedItem>>>? completedCandidates = null,
        Lazy<Task<List<DownloadingItem>>>? activeCandidates = null) =>
        (await ResolveAsync(
            requestedItem,
            strategy,
            cancellationToken,
            completedCandidates,
            activeCandidates).ConfigureAwait(true)).IsFullyCovered;

    public async Task<List<DownloadingItem>> LoadActiveCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var startup = await _projectionStore
            .GetDownloadingStateAsync(cancellationToken)
            .ConfigureAwait(true);
        return new List<DownloadingItem>(startup.Projections);
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

    private void MergeLiveActiveCandidates(List<DownloadingItem> activeCandidates)
    {
        var candidateIds = activeCandidates
            .Select(static item => item.DownloadBase.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in _downloadLists.Downloading.ToArray())
        {
            if (candidateIds.Add(item.DownloadBase.Id))
            {
                activeCandidates.Add(item);
            }
        }
    }

    private static string GetTaskId(DownloadedItem item) =>
        item.HistoryRecord?.Id.Value ?? item.DownloadBase.Id;

    private async Task<bool> ShouldRedownloadAsync(
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
            return false;
        }

        await _projectionStore
            .RemoveDownloadedAsync(item, cancellationToken)
            .ConfigureAwait(true);
        _downloadLists.RemoveDownloaded(item);
        return true;
    }

}

internal sealed record DownloadDuplicateResolution(
    DownloadContentSelection RemainingContent,
    bool AllowExistingBasePath)
{
    public bool IsFullyCovered => !RemainingContent.HasAnyRequestedAction;

    public static DownloadDuplicateResolution FullyCovered(
        DownloadContentSelection requestedContent) =>
        new(DownloadContentSelection.None, requestedContent.HasAnyRequestedAction);
}
