using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;

namespace DownKyi.Services.Download;

internal sealed record DownloadContentConflictPrompt(
    string PageName,
    DownloadContentConflict Conflict,
    string? ApiFailure = null);

internal static class DownloadContentConflictDialogContract
{
    internal const string PromptParameter = "prompt";
    internal const string DecisionParameter = "decision";

    public static AppDialogRequest CreateRequest(DownloadContentConflictPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new AppDialogRequest(
            AppDialog.DownloadContentConflict,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PromptParameter] = prompt
            });
    }

    public static IReadOnlyDictionary<string, object?> Encode(
        DownloadContentConflictDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DecisionParameter] = decision
        };
    }

    public static async Task<DownloadContentConflictDecision> ShowAsync(
        IAppDialogService dialogService,
        DownloadContentConflictPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        var result = await dialogService
            .ShowAsync(CreateRequest(prompt), cancellationToken)
            .ConfigureAwait(true);
        if (result.Outcome != AppDialogOutcome.Accepted)
        {
            throw new InvalidOperationException(
                "Download content conflict dialog requires an explicit decision.");
        }

        if (!result.Parameters.TryGetValue(DecisionParameter, out var value)
            || value is not DownloadContentConflictDecision decision)
        {
            throw new InvalidOperationException(
                "Accepted download content conflict result is missing a valid decision.");
        }

        return decision;
    }
}
