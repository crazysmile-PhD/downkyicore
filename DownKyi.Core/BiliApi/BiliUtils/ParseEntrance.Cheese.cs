using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    public static bool IsCheeseSeasonUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(GetCheeseId(input), "ss");
    }

    public static long GetCheeseSeasonId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsCheeseSeasonUrl(input)
            ? Number.GetInt(GetCheeseId(input).Remove(0, 2))
            : -1;
    }

    public static bool IsCheeseEpisodeUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(GetCheeseId(input), "ep");
    }

    public static long GetCheeseEpisodeId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsCheeseEpisodeUrl(input)
            ? Number.GetInt(GetCheeseId(input).Remove(0, 2))
            : -1;
    }
}
