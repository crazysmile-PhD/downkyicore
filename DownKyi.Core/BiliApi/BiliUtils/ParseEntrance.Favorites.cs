using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static partial class ParseEntrance
{
    public static bool IsFavoritesId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ml");
    }

    public static bool IsFavoritesUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsFavoritesUrl1(input) || IsFavoritesUrl2(input) || IsFavoritesUrl3(input);
    }

    public static long GetFavoritesId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsFavoritesId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        if (IsFavoritesUrl1(input))
        {
            return Number.GetInt(GetId(input, FavoritesUrl1).Remove(0, 2));
        }

        if (IsFavoritesUrl2(input))
        {
            return Number.GetInt(GetId(input, FavoritesUrl2).Remove(0, 2).Split('/')[0]);
        }

        return IsFavoritesUrl3(input)
            ? Number.GetInt(GetId(input, FavoritesUrl3).Remove(0, 2))
            : -1;
    }

    private static bool IsFavoritesUrl1(string input)
    {
        return IsFavoritesId(GetId(input, FavoritesUrl1));
    }

    private static bool IsFavoritesUrl2(string input)
    {
        return IsFavoritesId(GetId(input, FavoritesUrl2).Split('/')[0]);
    }

    private static bool IsFavoritesUrl3(string input)
    {
        return IsFavoritesId(GetId(input, FavoritesUrl3));
    }
}
