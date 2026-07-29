using System.Text.RegularExpressions;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    private static bool IsUrl(string input)
    {
        return input.StartsWith("http://", StringComparison.Ordinal)
            || input.StartsWith("https://", StringComparison.Ordinal);
    }

    private static string EnableHttps(string url)
    {
        return IsUrl(url)
            ? url.Replace("http://", "https://", StringComparison.Ordinal)
            : url;
    }

    private static string DeleteUrlParam(string url)
    {
        var path = url.Split('?')[0];
        return path.EndsWith('/') ? path.TrimEnd('/') : path;
    }

    private static string GetVideoId(string input)
    {
        return GetId(input, VideoUrl);
    }

    private static string GetBangumiId(string input)
    {
        var id = GetId(input, BangumiUrl);
        return !string.IsNullOrEmpty(id) ? id : GetId(input, BangumiMediaUrl);
    }

    private static string GetCheeseId(string input)
    {
        return GetId(input, CheeseUrl);
    }

    private static bool IsIntId(string input, string prefix)
    {
        return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(input.Remove(0, 2), @"^\d+$");
    }

    private static string GetId(string input, string baseUrl)
    {
        if (!IsUrl(input))
        {
            return string.Empty;
        }

        var url = DeleteUrlParam(EnableHttps(input))
            .Replace(ShareWwwUrl, WwwUrl, StringComparison.Ordinal)
            .Replace(MobileUrl, WwwUrl, StringComparison.Ordinal);

        url = url.Contains("b23.tv/ss", StringComparison.Ordinal)
            || url.Contains("b23.tv/ep", StringComparison.Ordinal)
                ? url.Replace(ShortUrl, BangumiUrl, StringComparison.Ordinal)
                : url.Replace(ShortUrl, VideoUrl, StringComparison.Ordinal);

        return url.StartsWith(baseUrl, StringComparison.Ordinal)
            ? url.Replace(baseUrl, string.Empty, StringComparison.Ordinal)
            : string.Empty;
    }
}
