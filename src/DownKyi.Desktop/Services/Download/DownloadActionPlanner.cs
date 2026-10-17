using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal enum DownloadPlanningStopReason
{
    NoContentRequested,
    NoPagesSelected,
    NoAvailableContent,
    SkippedByUser
}

internal sealed class DownloadActionPlanner
{
    private readonly DownloadContentConflictResolver _conflictResolver;

    public DownloadActionPlanner(DownloadContentConflictResolver conflictResolver)
    {
        _conflictResolver = conflictResolver
            ?? throw new ArgumentNullException(nameof(conflictResolver));
    }

    public async Task<FinalizedDownload> PlanAsync(
        DownloadContentSelection requestedContent,
        PreparedDownload preparedDownload,
        bool isAll,
        DownloadContentConflictChoices choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedDownload);
        var finalized = await PlanBatchAsync(
            requestedContent,
            [preparedDownload],
            isAll,
            choices,
            cancellationToken).ConfigureAwait(true);
        return finalized[0];
    }

    public async Task<IReadOnlyList<FinalizedDownload>> PlanBatchAsync(
        DownloadContentSelection requestedContent,
        IReadOnlyList<PreparedDownload> preparedDownloads,
        bool isAll,
        DownloadContentConflictChoices choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(preparedDownloads);
        ArgumentNullException.ThrowIfNull(choices);
        cancellationToken.ThrowIfCancellationRequested();
        var preparedDownloadSnapshot = preparedDownloads.ToArray();

        var actionableContent = requestedContent.SubtitleTrackSelection ==
                                DownloadSubtitleTrackSelection.NoTracksSelected
            ? requestedContent with
            {
                Subtitle = false,
                SelectedSubtitleTrackIds = null,
                DefaultSubtitleTrackId = null
            }
            : requestedContent;
        if (!actionableContent.HasAnyRequestedAction)
        {
            return preparedDownloadSnapshot.Select(preparedDownload => EmptyPlan(
                    preparedDownload,
                    DownloadPlanningStopReason.NoContentRequested,
                    candidateCount: 0,
                    skippedCount: 0))
                .ToArray();
        }

        var qualityItems = CollectQualitySubstitutionItems(
            actionableContent,
            preparedDownloadSnapshot,
            isAll);
        DownloadContentConflictAction? qualityAction = choices.TryGetQualitySubstitutionBatchAction(
            out var rememberedQualityAction)
            ? rememberedQualityAction
            : null;
        if (qualityItems.Count > 0 && qualityAction == null)
        {
            qualityAction = choices.RememberQualitySubstitutionBatchAction(
                await _conflictResolver
                    .ResolveQualitySubstitutionBatchAsync(
                        qualityItems,
                        choices.QualitySubstitutionDecisionAppliesToRemainingItems,
                        cancellationToken)
                    .ConfigureAwait(true));
        }

        var results = new List<FinalizedDownload>(preparedDownloadSnapshot.Length);
        foreach (var preparedDownload in preparedDownloadSnapshot)
        {
            results.Add(await PlanPreparedDownloadAsync(
                requestedContent,
                actionableContent,
                preparedDownload,
                isAll,
                choices,
                qualityAction,
                cancellationToken).ConfigureAwait(true));
        }

        return results;
    }

    private async Task<FinalizedDownload> PlanPreparedDownloadAsync(
        DownloadContentSelection requestedContent,
        DownloadContentSelection actionableContent,
        PreparedDownload preparedDownload,
        bool isAll,
        DownloadContentConflictChoices choices,
        DownloadContentConflictAction? qualityAction,
        CancellationToken cancellationToken)
    {
        var candidateCount = 0;
        var unavailableCount = 0;
        var skippedCount = 0;
        var sections = new List<FinalizedDownloadSection>(preparedDownload.Sections.Count);
        foreach (var preparedSection in preparedDownload.Sections)
        {
            var pages = new List<FinalizedDownloadPage>(preparedSection.Pages.Count);
            foreach (var preparedPage in preparedSection.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = preparedPage.Page;
                if (!isAll && !page.IsSelected)
                {
                    continue;
                }

                candidateCount++;
                var conflict = DownloadContentConflict.Find(
                    actionableContent,
                    preparedPage.AvailableMedia,
                    preparedPage.QualitySubstitutions);
                DownloadPageResolution resolution;
                if (conflict == null)
                {
                    resolution = DownloadPageResolution.Finalized(actionableContent);
                }
                else if (!conflict.HasAvailableContent)
                {
                    resolution = DownloadPageResolution.NoAvailableContent();
                }
                else if (conflict.IsQualitySubstitutionOnly)
                {
                    resolution = qualityAction switch
                    {
                        DownloadContentConflictAction.UseAvailableContent =>
                            DownloadPageResolution.Finalized(conflict.AvailableContent),
                        DownloadContentConflictAction.SkipPage =>
                            DownloadPageResolution.SkippedByUser(),
                        _ => throw new InvalidOperationException(
                            "A collected quality substitution requires one batch decision.")
                    };
                }
                else
                {
                    resolution = await _conflictResolver.ResolveAsync(
                        page.Name,
                        actionableContent,
                        preparedPage,
                        choices,
                        cancellationToken)
                    .ConfigureAwait(true);
                }

                if (resolution.FinalizedContent is { } finalizedContent)
                {
                    pages.Add(new FinalizedDownloadPage(
                        page,
                        preparedPage.VideoQuality,
                        preparedPage.AudioQualityFormat,
                        preparedPage.AvailableMedia,
                        requestedContent,
                        finalizedContent));
                }
                else
                {
                    switch (resolution.StopReason)
                    {
                        case DownloadPlanningStopReason.NoAvailableContent:
                            unavailableCount++;
                            break;
                        case DownloadPlanningStopReason.SkippedByUser:
                            skippedCount++;
                            break;
                        default:
                            throw new InvalidOperationException(
                                "A page resolution must contain finalized content or a stop reason.");
                    }
                }
            }

            sections.Add(new FinalizedDownloadSection(preparedSection.Section, pages));
        }

        var finalizedCount = candidateCount - unavailableCount - skippedCount;
        var stopReason = finalizedCount > 0
            ? (DownloadPlanningStopReason?)null
            : candidateCount == 0
                ? DownloadPlanningStopReason.NoPagesSelected
                : skippedCount > 0
                    ? DownloadPlanningStopReason.SkippedByUser
                    : DownloadPlanningStopReason.NoAvailableContent;
        return new FinalizedDownload(
            preparedDownload.Video,
            sections,
            stopReason,
            candidateCount,
            skippedCount + unavailableCount);
    }

    private static List<DownloadContentConflictPromptItem> CollectQualitySubstitutionItems(
        DownloadContentSelection actionableContent,
        IReadOnlyList<PreparedDownload> preparedDownloads,
        bool isAll)
    {
        var items = new List<DownloadContentConflictPromptItem>();
        foreach (var preparedDownload in preparedDownloads)
        {
            foreach (var preparedPage in preparedDownload.Sections.SelectMany(section => section.Pages))
            {
                var page = preparedPage.Page;
                if (!isAll && !page.IsSelected)
                {
                    continue;
                }

                var conflict = DownloadContentConflict.Find(
                    actionableContent,
                    preparedPage.AvailableMedia,
                    preparedPage.QualitySubstitutions);
                if (conflict is not { IsQualitySubstitutionOnly: true, HasAvailableContent: true })
                {
                    continue;
                }

                items.Add(new DownloadContentConflictPromptItem(
                    CreatePageDisplayName(preparedDownload.Video.Title, page.Name),
                    conflict));
            }
        }

        return items;
    }

    private static string CreatePageDisplayName(string videoTitle, string pageName)
    {
        var title = videoTitle.Trim();
        var page = pageName.Trim();
        if (title.Length == 0)
        {
            return page;
        }

        if (page.Length == 0 || string.Equals(title, page, StringComparison.Ordinal))
        {
            return title;
        }

        return $"{title} · {page}";
    }

    private static FinalizedDownload EmptyPlan(
        PreparedDownload preparedDownload,
        DownloadPlanningStopReason stopReason,
        int candidateCount,
        int skippedCount) =>
        new(
            preparedDownload.Video,
            preparedDownload.Sections
                .Select(static section =>
                    new FinalizedDownloadSection(section.Section, []))
                .ToArray(),
            stopReason,
            candidateCount,
            skippedCount);
}
