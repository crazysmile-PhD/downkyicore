using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    public static bool IsAvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "av");
    }

    public static bool IsAvUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsAvId(GetVideoId(input));
    }

    public static long GetAvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsAvId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        return IsAvUrl(input)
            ? Number.GetInt(GetVideoId(input).Remove(0, 2))
            : -1;
    }

    public static bool IsBvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.StartsWith("BV", StringComparison.Ordinal) && input.Length == 12;
    }

    public static bool IsBvUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsBvId(GetVideoId(input));
    }

    public static string GetBvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBvId(input))
        {
            return input;
        }

        return IsBvUrl(input) ? GetVideoId(input) : string.Empty;
    }
}
