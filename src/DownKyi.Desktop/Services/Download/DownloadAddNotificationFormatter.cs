using System;
using System.Globalization;
using DownKyi.Utils;

namespace DownKyi.Services.Download;

internal static class DownloadAddNotificationFormatter
{
    public static string Format(DownloadAddResult result)
    {
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

        return result.Outcome switch
        {
            DownloadAddOutcome.Added => FormatCounts(
                result.AddedCount,
                result.DuplicateCount,
                result.FailedCount,
                result.SkippedCount),
            DownloadAddOutcome.AllDuplicate =>
                DictionaryResource.GetString("TipAlreadyToAddDownloading"),
            DownloadAddOutcome.NoAvailableContent =>
                DictionaryResource.GetString("TipAddDownloadingUnavailable"),
            DownloadAddOutcome.SkippedByUser =>
                DictionaryResource.GetString("TipAddDownloadingSkippedByChoice"),
            DownloadAddOutcome.Failed =>
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
}
