using System.Globalization;

namespace DownKyi.Services.Download;

internal static class DownloadTransferKey
{
    public static string Create(int streamId, string codec)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{streamId}_{codec}");
    }
}
