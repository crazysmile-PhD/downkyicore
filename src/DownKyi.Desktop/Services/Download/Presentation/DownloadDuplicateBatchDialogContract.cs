using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;

namespace DownKyi.Services.Download;

internal enum DownloadDuplicateBatchAction
{
    SkipCompleted,
    RedownloadCompleted
}

internal sealed record DownloadDuplicateBatchPrompt
{
    public DownloadDuplicateBatchPrompt(
        IReadOnlyList<string> completedItemNames,
        int activeDuplicateCount)
    {
        ArgumentNullException.ThrowIfNull(completedItemNames);
        if (completedItemNames.Count == 0)
        {
            throw new ArgumentException(
                "A duplicate batch prompt requires at least one completed item.",
                nameof(completedItemNames));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(activeDuplicateCount);
        CompletedItemNames = Array.AsReadOnly(completedItemNames.ToArray());
        ActiveDuplicateCount = activeDuplicateCount;
    }

    public IReadOnlyList<string> CompletedItemNames { get; }

    public int ActiveDuplicateCount { get; }
}

internal static class DownloadDuplicateBatchDialogContract
{
    internal const string PromptParameter = "prompt";
    internal const string DecisionParameter = "decision";

    public static AppDialogRequest CreateRequest(DownloadDuplicateBatchPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new AppDialogRequest(
            AppDialog.DownloadDuplicateBatch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PromptParameter] = prompt
            });
    }

    public static IReadOnlyDictionary<string, object?> Encode(
        DownloadDuplicateBatchAction action) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DecisionParameter] = action
        };

    public static async Task<DownloadDuplicateBatchAction> ShowAsync(
        IAppDialogService dialogService,
        DownloadDuplicateBatchPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        var result = await dialogService
            .ShowAsync(CreateRequest(prompt), cancellationToken)
            .ConfigureAwait(true);
        if (result.Outcome != AppDialogOutcome.Accepted
            || !result.Parameters.TryGetValue(DecisionParameter, out var value)
            || value is not DownloadDuplicateBatchAction action)
        {
            throw new InvalidOperationException(
                "Download duplicate confirmation requires an explicit decision.");
        }

        return action;
    }
}
