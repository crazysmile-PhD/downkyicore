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
                DownloadPlanningStopReason.NoContentRequested,
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
                if (resolution.FinalizedContent is { } finalizedContent)
                {
                    pages.Add(new FinalizedDownloadPage(
                        page,
                        page.VideoQuality,
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
