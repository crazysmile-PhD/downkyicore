using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal enum DownloadContentConflictAction
{
    UseAvailableMedia,
    SkipPage
}

internal sealed record DownloadContentConflict(
    DownloadContentSelection RequestedContent,
    DownloadMediaCapabilities AvailableMedia,
    DownloadContentSelection AvailableContent)
{
    public bool HasAvailableMedia => AvailableContent.Audio || AvailableContent.Video;

    public static DownloadContentConflict? Find(
        DownloadContentSelection requestedContent,
        DownloadMediaCapabilities availableMedia)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(availableMedia);

        if (availableMedia.Supports(requestedContent))
        {
            return null;
        }

        availableMedia.TryGetCompatibleContent(requestedContent, out var availableContent);
        return new DownloadContentConflict(
            requestedContent,
            availableMedia,
            availableContent);
    }
}

internal sealed record DownloadContentConflictDecision(
    DownloadContentConflictAction Action,
    bool ApplyToAll);

internal sealed class DownloadContentConflictChoices
{
    private readonly Dictionary<DownloadContentConflict, DownloadContentConflictAction> _choices = [];

    public bool TryGet(
        DownloadContentConflict conflict,
        out DownloadContentConflictAction action) => _choices.TryGetValue(conflict, out action);

    public void Remember(
        DownloadContentConflict conflict,
        DownloadContentConflictAction action) => _choices[conflict] = action;
}

internal sealed class DownloadContentConflictResolver
{
    private readonly IAppDialogService _dialogService;

    public DownloadContentConflictResolver(IAppDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<FinalizedDownload> ResolveAsync(
        DownloadContentSelection requestedContent,
        PreparedDownload preparedDownload,
        bool isAll,
        DownloadContentConflictChoices choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(preparedDownload);
        ArgumentNullException.ThrowIfNull(choices);

        var sections = new List<FinalizedDownloadSection>(preparedDownload.Sections.Count);
        foreach (var preparedSection in preparedDownload.Sections)
        {
            var pages = new List<FinalizedDownloadPage>(preparedSection.Pages.Count);
            foreach (var preparedPage in preparedSection.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = preparedPage.Page;
                if ((!isAll && !page.IsSelected) || !page.HasPlayback || page.VideoQuality == null)
                {
                    continue;
                }

                var conflict = DownloadContentConflict.Find(
                    requestedContent,
                    preparedPage.AvailableMedia);
                if (conflict == null)
                {
                    pages.Add(new FinalizedDownloadPage(page, page.VideoQuality, requestedContent));
                    continue;
                }

                if (!conflict.HasAvailableMedia)
                {
                    continue;
                }

                var action = await ResolveActionAsync(
                    page.Name,
                    conflict,
                    choices,
                    cancellationToken).ConfigureAwait(true);
                if (action == DownloadContentConflictAction.UseAvailableMedia)
                {
                    pages.Add(new FinalizedDownloadPage(
                        page,
                        page.VideoQuality,
                        conflict.AvailableContent));
                }
            }

            sections.Add(new FinalizedDownloadSection(preparedSection.Section, pages));
        }

        return new FinalizedDownload(preparedDownload.Video, sections);
    }

    private async Task<DownloadContentConflictAction> ResolveActionAsync(
        string pageName,
        DownloadContentConflict conflict,
        DownloadContentConflictChoices choices,
        CancellationToken cancellationToken)
    {
        if (choices.TryGet(conflict, out var rememberedAction))
        {
            return rememberedAction;
        }

        var decision = await DownloadContentConflictDialogContract.ShowAsync(
            _dialogService,
            new DownloadContentConflictPrompt(pageName, conflict),
            cancellationToken).ConfigureAwait(true);
        if (decision.ApplyToAll)
        {
            choices.Remember(conflict, decision.Action);
        }

        return decision.Action;
    }
}
