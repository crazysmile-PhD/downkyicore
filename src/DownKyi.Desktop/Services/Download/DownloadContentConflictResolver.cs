using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal enum DownloadContentConflictAction
{
    UseAvailableContent,
    SkipPage
}

internal sealed record DownloadContentConflict(
    DownloadContentSelection RequestedContent,
    DownloadMediaCapabilities AvailableMedia,
    DownloadContentSelection AvailableContent,
    DownloadQualitySubstitutions QualitySubstitutions)
{
    public bool HasAvailableContent => AvailableContent.HasAnyRequestedAction;

    public static DownloadContentConflict? Find(
        DownloadContentSelection requestedContent,
        DownloadMediaCapabilities availableMedia,
        DownloadQualitySubstitutions qualitySubstitutions)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(availableMedia);
        ArgumentNullException.ThrowIfNull(qualitySubstitutions);

        var requestedMediaAvailable = availableMedia.Supports(requestedContent);
        _ = availableMedia.TryGetCompatibleContent(requestedContent, out var availableContent);
        var relevantSubstitutions = qualitySubstitutions.For(availableContent);
        return (requestedMediaAvailable, relevantSubstitutions.HasAny) switch
        {
            (true, false) => null,
            _ => new DownloadContentConflict(
                requestedContent,
                availableMedia,
                availableContent,
                relevantSubstitutions)
        };
    }
}

internal sealed record DownloadContentConflictDecision(
    DownloadContentConflictAction Action,
    bool ApplyToAll);

internal sealed class DownloadContentConflictChoices
{
    private readonly Dictionary<DownloadContentConflictKey, DownloadContentConflictAction> _choices = [];

    public bool TryGet(
        DownloadContentConflict conflict,
        out DownloadContentConflictAction action) =>
        _choices.TryGetValue(DownloadContentConflictKey.From(conflict), out action);

    public DownloadContentConflictAction Remember(
        DownloadContentConflict conflict,
        DownloadContentConflictAction action) =>
        (conflict.QualitySubstitutions.HasAny, action) switch
        {
            (true, DownloadContentConflictAction.SkipPage) => action,
            _ => Store(DownloadContentConflictKey.From(conflict), action)
        };

    private DownloadContentConflictAction Store(
        DownloadContentConflictKey key,
        DownloadContentConflictAction action)
    {
        _choices[key] = action;
        return action;
    }

    private abstract record DownloadContentConflictKey
    {
        public static DownloadContentConflictKey From(DownloadContentConflict conflict)
        {
            ArgumentNullException.ThrowIfNull(conflict);
            return conflict.QualitySubstitutions.HasAny switch
            {
                true => new QualitySubstitutionConflictKey(
                    conflict.RequestedContent,
                    conflict.AvailableContent),
                false => new MediaAvailabilityConflictKey(
                    conflict.RequestedContent,
                    conflict.AvailableContent,
                    conflict.AvailableMedia.SupportedModes,
                    conflict.AvailableMedia.VideoSelectionRequired)
            };
        }

        private sealed record QualitySubstitutionConflictKey(
            DownloadContentSelection RequestedContent,
            DownloadContentSelection AvailableContent) : DownloadContentConflictKey;

        private sealed record MediaAvailabilityConflictKey(
            DownloadContentSelection RequestedContent,
            DownloadContentSelection AvailableContent,
            DownloadMediaOutputModes SupportedModes,
            bool VideoSelectionRequired) : DownloadContentConflictKey;
    }
}

internal sealed class DownloadContentConflictResolver
{
    private readonly IAppDialogService _dialogService;

    public DownloadContentConflictResolver(IAppDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<DownloadPageResolution> ResolveAsync(
        string pageName,
        DownloadContentSelection requestedContent,
        PreparedDownloadPage preparedPage,
        DownloadContentConflictChoices choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(preparedPage);
        ArgumentNullException.ThrowIfNull(choices);
        cancellationToken.ThrowIfCancellationRequested();

        var conflict = DownloadContentConflict.Find(
            requestedContent,
            preparedPage.AvailableMedia,
            preparedPage.QualitySubstitutions);
        if (conflict == null)
        {
            return DownloadPageResolution.Finalized(requestedContent);
        }

        if (!conflict.HasAvailableContent)
        {
            return DownloadPageResolution.NoAvailableContent();
        }

        var action = await ResolveActionAsync(
            pageName,
            conflict,
            choices,
            cancellationToken).ConfigureAwait(true);
        return action == DownloadContentConflictAction.UseAvailableContent
            ? DownloadPageResolution.Finalized(conflict.AvailableContent)
            : DownloadPageResolution.SkippedByUser();
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
        return decision.ApplyToAll switch
        {
            true => choices.Remember(conflict, decision.Action),
            false => decision.Action
        };
    }
}

internal sealed record DownloadPageResolution(
    DownloadContentSelection? FinalizedContent,
    DownloadPlanningStopReason? StopReason)
{
    public static DownloadPageResolution Finalized(DownloadContentSelection content) =>
        new(content, null);

    public static DownloadPageResolution NoAvailableContent() =>
        new(null, DownloadPlanningStopReason.NoAvailableContent);

    public static DownloadPageResolution SkippedByUser() =>
        new(null, DownloadPlanningStopReason.SkippedByUser);
}
