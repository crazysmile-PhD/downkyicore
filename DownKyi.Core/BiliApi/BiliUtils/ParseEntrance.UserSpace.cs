using System.Globalization;
using System.Text.RegularExpressions;
using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    public static bool IsUserId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(input.Remove(0, 4), @"^\d+$");
        }

        return input.StartsWith("uid", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(input.Remove(0, 3), @"^\d+$");
    }

    public static bool IsUserUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return TryGetUserSpaceId(input, out _);
    }

    public static long GetUserId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
        {
            return Number.GetInt(input.Remove(0, 4));
        }

        if (input.StartsWith("uid", StringComparison.OrdinalIgnoreCase))
        {
            return Number.GetInt(input.Remove(0, 3));
        }

        return TryGetUserSpaceId(input, out var mid) ? mid : -1;
    }

    private static bool TryGetUserSpaceId(string input, out long mid)
    {
        mid = -1;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.Equals(uri.Host, "space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 1
            && long.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out mid);
    }
}
