using System.Globalization;
using DownKyi.Utils;

namespace DownKyi.Services.Media;

internal static class ContentDownloadNotificationFormatter
{
    public static string Format(ContentDownloadBatchResult result)
    {
        if (result.SkippedCount > 0)
        {
            var resourceKey = result.AddedCount > 0
                ? "TipAddDownloadingFinishedWithSkipped"
                : "TipAddDownloadingSkipped";
            return string.Format(
                CultureInfo.CurrentCulture,
                DictionaryResource.GetString(resourceKey),
                result.AddedCount,
                result.SkippedCount);
        }

        return result.AddedCount <= 0
            ? DictionaryResource.GetString("TipAddDownloadingZero")
            : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}" +
              $"{result.AddedCount}" +
              $"{DictionaryResource.GetString("TipAddDownloadingFinished2")}";
    }
}
