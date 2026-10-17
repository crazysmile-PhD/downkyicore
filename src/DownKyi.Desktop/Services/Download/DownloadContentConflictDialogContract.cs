using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;

namespace DownKyi.Services.Download;

internal enum DownloadContentConflictPromptMode
{
    SingleConflict,
    QualitySubstitutionBatch
}

internal sealed record DownloadContentConflictPromptItem(
    string PageName,
    DownloadContentConflict Conflict);

internal sealed record DownloadContentConflictPrompt
{
    public DownloadContentConflictPrompt(
        string pageName,
        DownloadContentConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(pageName);
        ArgumentNullException.ThrowIfNull(conflict);
        Mode = DownloadContentConflictPromptMode.SingleConflict;
        Items = Array.AsReadOnly(
            [new DownloadContentConflictPromptItem(pageName, conflict)]);
    }

    private DownloadContentConflictPrompt(
        DownloadContentConflictPromptMode mode,
        IReadOnlyList<DownloadContentConflictPromptItem> items,
        bool appliesToRemainingItems)
    {
        Mode = mode;
        Items = Array.AsReadOnly(items.ToArray());
        AppliesToRemainingItems = appliesToRemainingItems;
    }

    public DownloadContentConflictPromptMode Mode { get; }

    public IReadOnlyList<DownloadContentConflictPromptItem> Items { get; }

    public bool AppliesToRemainingItems { get; }

    public string PageName => GetSingleItem().PageName;

    public DownloadContentConflict Conflict => GetSingleItem().Conflict;

    public static DownloadContentConflictPrompt QualitySubstitutionBatch(
        IReadOnlyList<DownloadContentConflictPromptItem> items,
        bool appliesToRemainingItems = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException(
                "A quality substitution batch requires at least one item.",
                nameof(items));
        }

        if (items.Any(item => item == null || !item.Conflict.IsQualitySubstitutionOnly))
        {
            throw new ArgumentException(
                "A quality substitution batch can contain only pure quality substitutions.",
                nameof(items));
        }

        return new DownloadContentConflictPrompt(
            DownloadContentConflictPromptMode.QualitySubstitutionBatch,
            items,
            appliesToRemainingItems);
    }

    private DownloadContentConflictPromptItem GetSingleItem()
    {
        if (Items.Count != 1)
        {
            throw new InvalidOperationException("A batch conflict prompt does not have one page.");
        }

        return Items[0];
    }
}

internal static class DownloadContentConflictDialogContract
{
    internal const string PromptParameter = "prompt";
    internal const string DecisionParameter = "decision";

    public static AppDialogRequest CreateRequest(DownloadContentConflictPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Items.Count == 0)
        {
            throw new ArgumentException("A download conflict prompt requires at least one item.", nameof(prompt));
        }

        if (prompt.Mode == DownloadContentConflictPromptMode.SingleConflict
            && prompt.Items.Count != 1)
        {
            throw new ArgumentException(
                "A single download conflict prompt requires exactly one item.",
                nameof(prompt));
        }

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
