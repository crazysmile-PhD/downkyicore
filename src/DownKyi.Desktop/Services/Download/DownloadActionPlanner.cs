using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal enum DownloadActionPlanOutcome
{
    Ready,
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
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(preparedDownload);
        ArgumentNullException.ThrowIfNull(choices);

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
            return EmptyPlan(
                preparedDownload,
                DownloadActionPlanOutcome.NoContentRequested,
                candidateCount: 0,
                skippedCount: 0);
        }

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
                var resolution = await _conflictResolver
                    .ResolveAsync(
                        page.Name,
                        actionableContent,
                        preparedPage,
                        choices,
                        cancellationToken)
                    .ConfigureAwait(true);
                switch (resolution.Status)
                {
                    case DownloadPageResolutionStatus.Finalized:
                        var finalizedContent = resolution.FinalizedContent
                            ?? throw new InvalidOperationException(
                                "A finalized download page requires finalized content.");
                        pages.Add(new FinalizedDownloadPage(
                            page,
                            page.VideoQuality,
                            requestedContent,
                            finalizedContent));
                        break;
                    case DownloadPageResolutionStatus.NoAvailableContent:
                        unavailableCount++;
                        break;
                    case DownloadPageResolutionStatus.SkippedByUser:
                        skippedCount++;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported page resolution status: {resolution.Status:G}.");
                }
            }

            sections.Add(new FinalizedDownloadSection(preparedSection.Section, pages));
        }

        var finalizedCount = candidateCount - unavailableCount - skippedCount;
        var outcome = finalizedCount > 0
            ? DownloadActionPlanOutcome.Ready
            : candidateCount == 0
                ? DownloadActionPlanOutcome.NoPagesSelected
                : skippedCount > 0
                    ? DownloadActionPlanOutcome.SkippedByUser
                    : DownloadActionPlanOutcome.NoAvailableContent;
        return new FinalizedDownload(
            preparedDownload.Video,
            sections,
            outcome,
            candidateCount,
            skippedCount + unavailableCount);
    }

    private static FinalizedDownload EmptyPlan(
        PreparedDownload preparedDownload,
        DownloadActionPlanOutcome outcome,
        int candidateCount,
        int skippedCount) =>
        new(
            preparedDownload.Video,
            preparedDownload.Sections
                .Select(static section =>
                    new FinalizedDownloadSection(section.Section, []))
                .ToArray(),
            outcome,
            candidateCount,
            skippedCount);
}
