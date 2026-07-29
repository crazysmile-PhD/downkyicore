using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    public static bool IsBangumiSeasonId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ss");
    }

    public static bool IsBangumiSeasonUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsBangumiSeasonId(GetBangumiId(input));
    }

    public static long GetBangumiSeasonId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiSeasonId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        return IsBangumiSeasonUrl(input)
            ? Number.GetInt(GetBangumiId(input).Remove(0, 2))
            : -1;
    }

    public static bool IsBangumiEpisodeId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ep");
    }

    public static bool IsBangumiEpisodeUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsBangumiEpisodeId(GetBangumiId(input));
    }

    public static long GetBangumiEpisodeId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiEpisodeId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        return IsBangumiEpisodeUrl(input)
            ? Number.GetInt(GetBangumiId(input).Remove(0, 2))
            : -1;
    }

    public static bool IsBangumiMediaId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "md");
    }

    public static bool IsBangumiMediaUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsBangumiMediaId(GetBangumiId(input));
    }

    public static long GetBangumiMediaId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiMediaId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        return IsBangumiMediaUrl(input)
            ? Number.GetInt(GetBangumiId(input).Remove(0, 2))
            : -1;
    }
}
