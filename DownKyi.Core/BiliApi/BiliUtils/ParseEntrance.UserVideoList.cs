using System.Globalization;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    /// <summary>
    /// 是否为仅以UP主MID标识的全部投稿列表URL。
    /// 带有sid的URL表示系列列表，不可误判为全部投稿。
    /// </summary>
    public static bool IsUserVideoListUrl(string input)
    {
        return GetUserVideoListId(input) > 0;
    }

    /// <summary>
    /// 获取全部投稿列表URL中的UP主MID。
    /// </summary>
    public static long GetUserVideoListId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsBilibiliWebHost(uri.Host)
            || HasNonEmptyQueryValue(uri.Query, "sid"))
        {
            return -1;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2
            && string.Equals(segments[0], "list", StringComparison.Ordinal)
            && long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mid)
            && mid > 0
                ? mid
                : -1;
    }

    private static bool IsBilibiliWebHost(string host)
    {
        return string.Equals(host, "bilibili.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www.bilibili.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "m.bilibili.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNonEmptyQueryValue(string query, string name)
    {
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase)
                && pair.Length == 2
                && !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(pair[1])))
            {
                return true;
            }
        }

        return false;
    }
}
