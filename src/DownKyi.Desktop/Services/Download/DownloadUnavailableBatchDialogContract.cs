using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;

namespace DownKyi.Services.Download;

internal enum DownloadUnavailableBatchAction
{
    SkipUnavailable,
    CancelBatch
}

internal enum DownloadUnavailableReason
{
    Unknown,
    AccountRequired,
    AccessRestrictedOrUnavailable,
    VideoUnavailable,
    NoPlaybackResource,
    NetworkFailure,
    RequestTimedOut,
    InvalidResponse,
    ApiRejected,
    NoDownloadData
}

internal sealed record DownloadUnavailableBatchPromptItem
{
    public DownloadUnavailableBatchPromptItem(
        string displayName,
        string source,
        DownloadUnavailableReason reason,
        int? apiCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        DisplayName = displayName;
        Source = source;
        Reason = reason;
        ApiCode = apiCode;
    }

    public string DisplayName { get; }

    public string Source { get; }

    public DownloadUnavailableReason Reason { get; }

    public int? ApiCode { get; }
}

internal sealed record DownloadUnavailableBatchPrompt
{
    public DownloadUnavailableBatchPrompt(
        IReadOnlyList<DownloadUnavailableBatchPromptItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException(
                "An unavailable batch prompt requires at least one item.",
                nameof(items));
        }

        Items = Array.AsReadOnly(items.ToArray());
    }

    public IReadOnlyList<DownloadUnavailableBatchPromptItem> Items { get; }
}

internal static class DownloadUnavailableBatchDialogContract
{
    internal const string PromptParameter = "prompt";
    internal const string DecisionParameter = "decision";

    public static AppDialogRequest CreateRequest(DownloadUnavailableBatchPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new AppDialogRequest(
            AppDialog.DownloadUnavailableBatch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PromptParameter] = prompt
            });
    }

    public static IReadOnlyDictionary<string, object?> Encode(
        DownloadUnavailableBatchAction action) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DecisionParameter] = action
        };

    public static async Task<DownloadUnavailableBatchAction> ShowAsync(
        IAppDialogService dialogService,
        DownloadUnavailableBatchPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        var result = await dialogService
            .ShowAsync(CreateRequest(prompt), cancellationToken)
            .ConfigureAwait(true);
        if (result.Outcome != AppDialogOutcome.Accepted
            || !result.Parameters.TryGetValue(DecisionParameter, out var value)
            || value is not DownloadUnavailableBatchAction action)
        {
            throw new InvalidOperationException(
                "Unavailable content confirmation requires an explicit decision.");
        }

        return action;
    }
}
