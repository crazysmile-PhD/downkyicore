using System;
using System.Globalization;
using DownKyi.Utils;

namespace DownKyi.Services.Download;

internal enum DownloadAddNotificationKind
{
    Success,
    Warning,
    Canceled,
    Error
}

internal readonly record struct DownloadAddNotification(
    DownloadAddNotificationKind Kind,
    string Message);

internal static class DownloadAddNotificationFormatter
{
    public static DownloadAddNotification Create(DownloadAddResult result)
    {
        return new DownloadAddNotification(
            result.Outcome switch
            {
                DownloadAddOutcome.Completed => DownloadAddNotificationKind.Success,
                DownloadAddOutcome.Partial or DownloadAddOutcome.Rejected =>
                    DownloadAddNotificationKind.Warning,
                DownloadAddOutcome.Canceled => DownloadAddNotificationKind.Canceled,
                DownloadAddOutcome.Failed => DownloadAddNotificationKind.Error,
                _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, null)
            },
            FormatMessage(result));
    }

    private static string FormatMessage(DownloadAddResult result)
    {
        if (result.Outcome == DownloadAddOutcome.Canceled && result.HasRecordedWork)
        {
            return FormatText(
                "TipAddDownloadingCanceledAfterProgress",
                FormatCounts(
                    result.AddedCount,
                    result.DuplicateCount,
                    result.FailedCount,
                    result.SkippedCount));
        }

        if (HasMixedCounts(
                result.AddedCount,
                result.DuplicateCount,
                result.FailedCount,
                result.SkippedCount))
        {
            return FormatCounts(
                result.AddedCount,
                result.DuplicateCount,
                result.FailedCount,
                result.SkippedCount);
        }

        if (result.AddedCount > 0)
        {
            return FormatCounts(
                result.AddedCount,
                result.DuplicateCount,
                result.FailedCount,
                result.SkippedCount);
        }

        if (result.IsAllDuplicate)
        {
            return DictionaryResource.GetString("TipAlreadyToAddDownloading");
        }

        return result.StopReason switch
        {
            DownloadAddStopReason.UnsupportedInput =>
                DictionaryResource.GetString("TipAddDownloadingUnsupportedInput"),
            DownloadAddStopReason.SelectionCanceled or DownloadAddStopReason.CancellationRequested =>
                DictionaryResource.GetString("TipAddDownloadingCanceled"),
            DownloadAddStopReason.SelectionUnavailable =>
                DictionaryResource.GetString("TipAddDownloadingSelectionUnavailable"),
            DownloadAddStopReason.NoAvailableContent =>
                DictionaryResource.GetString("TipAddDownloadingUnavailable"),
            DownloadAddStopReason.SkippedByUser =>
                DictionaryResource.GetString("TipAddDownloadingSkippedByChoice"),
            DownloadAddStopReason.RuntimeUnavailable or DownloadAddStopReason.TaskAdmissionFailed =>
                DictionaryResource.GetString("TipAddDownloadingFailed"),
            _ when result.FailedCount > 0 =>
                DictionaryResource.GetString("TipAddDownloadingFailed"),
            _ => DictionaryResource.GetString("TipAddDownloadingZero")
        };
    }

    public static string FormatCounts(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount)
    {
        if (HasMixedCounts(addedCount, duplicateCount, failedCount, skippedCount))
        {
            var resourceKey = addedCount > 0
                ? "TipAddDownloadingSummary"
                : "TipAddDownloadingNoAddedSummary";
            return addedCount > 0
                ? FormatCounts(
                    resourceKey,
                    addedCount,
                    duplicateCount,
                    skippedCount,
                    failedCount)
                : FormatCounts(resourceKey, duplicateCount, skippedCount, failedCount);
        }

        if (addedCount > 0)
        {
            return $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}" +
                   $"{addedCount}" +
                   $"{DictionaryResource.GetString("TipAddDownloadingFinished2")}";
        }

        if (duplicateCount > 0)
        {
            return DictionaryResource.GetString("TipAlreadyToAddDownloading");
        }

        if (failedCount > 0)
        {
            return DictionaryResource.GetString("TipAddDownloadingFailed");
        }

        if (skippedCount > 0)
        {
            return FormatCounts("TipAddDownloadingSkipped", 0, skippedCount);
        }

        return DictionaryResource.GetString("TipAddDownloadingZero");
    }

    private static bool HasMixedCounts(
        int addedCount,
        int duplicateCount,
        int failedCount,
        int skippedCount) =>
        (addedCount > 0 ? 1 : 0)
        + (duplicateCount > 0 ? 1 : 0)
        + (failedCount > 0 ? 1 : 0)
        + (skippedCount > 0 ? 1 : 0) > 1;

    private static string FormatCounts(string resourceKey, params int[] counts)
    {
        var message = DictionaryResource.GetString(resourceKey);
        for (var index = 0; index < counts.Length; index++)
        {
            message = message.Replace(
                $"{{{index}}}",
                counts[index].ToString(CultureInfo.CurrentCulture),
                StringComparison.Ordinal);
        }

        return message;
    }

    private static string FormatText(string resourceKey, string summary)
    {
        return DictionaryResource.GetString(resourceKey).Replace(
            "{0}",
            summary,
            StringComparison.Ordinal);
    }
}
