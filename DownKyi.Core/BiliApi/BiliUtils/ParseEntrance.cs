using System.Globalization;
using System.Text.RegularExpressions;
using DownKyi.Core.Utils.Validator;

namespace DownKyi.Core.BiliApi.BiliUtils;

/// <summary>
/// 解析输入的字符串<para/>
/// 支持的格式有：<para/>
/// av号：av170001, AV170001, https://www.bilibili.com/video/av170001 <para/>
/// BV号：BV17x411w7KC, https://www.bilibili.com/video/BV17x411w7KC, https://b23.tv/BV17x411w7KC <para/>
/// 番剧（电影、电视剧）ss号：ss32982, SS32982, https://www.bilibili.com/bangumi/play/ss32982 <para/>
/// 番剧（电影、电视剧）ep号：ep317925, EP317925, https://www.bilibili.com/bangumi/play/ep317925 <para/>
/// 番剧（电影、电视剧）md号：md28228367, MD28228367, https://www.bilibili.com/bangumi/media/md28228367 <para/>
/// 课程ss号：https://www.bilibili.com/cheese/play/ss205 <para/>
/// 课程ep号：https://www.bilibili.com/cheese/play/ep3489 <para/>
/// 收藏夹：ml1329019876, ML1329019876, https://www.bilibili.com/medialist/detail/ml1329019876, https://www.bilibili.com/medialist/play/ml1329019876/, https://www.bilibili.com/list/ml1329019876 <para/>
/// UP主视频列表：https://www.bilibili.com/list/3546801722362343 <para/>
/// 用户空间：uid928123, UID928123, uid:928123, UID:928123, https://space.bilibili.com/928123
/// </summary>
public static class ParseEntrance
{
    public static readonly string WwwUrl = "https://www.bilibili.com";
    public static readonly string ShareWwwUrl = "https://www.bilibili.com/s";
    public static readonly string ShortUrl = "https://b23.tv/";
    public static readonly string MobileUrl = "https://m.bilibili.com";

    public static readonly string SpaceUrl = "https://space.bilibili.com";

    public static readonly string VideoUrl = $"{WwwUrl}/video/";
    public static readonly string BangumiUrl = $"{WwwUrl}/bangumi/play/";
    public static readonly string BangumiMediaUrl = $"{WwwUrl}/bangumi/media/";
    public static readonly string CheeseUrl = $"{WwwUrl}/cheese/play/";
    public static readonly string FavoritesUrl1 = $"{WwwUrl}/medialist/detail/";
    public static readonly string FavoritesUrl2 = $"{WwwUrl}/medialist/play/";
    public static readonly string FavoritesUrl3 = $"{WwwUrl}/list/";

    #region 视频

    /// <summary>
    /// 是否为av id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsAvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "av");
    }

    /// <summary>
    /// 是否为av url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsAvUrl(string input)
    {
        string id = GetVideoId(input);
        return IsAvId(id);
    }

    /// <summary>
    /// 获取av id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetAvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsAvId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }
        else if (IsAvUrl(input))
        {
            return Number.GetInt(GetVideoId(input).Remove(0, 2));
        }
        else
        {
            return -1;
        }
    }

    /// <summary>
    /// 是否为bv id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBvId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.StartsWith("BV", StringComparison.Ordinal) && input.Length == 12;
    }

    /// <summary>
    /// 是否为bv url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBvUrl(string input)
    {
        string id = GetVideoId(input);
        return IsBvId(id);
    }

    /// <summary>
    /// 获取bv id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static string GetBvId(string input)
    {
        if (IsBvId(input))
        {
            return input;
        }
        else if (IsBvUrl(input))
        {
            return GetVideoId(input);
        }
        else
        {
            return "";
        }
    }

    #endregion

    #region 番剧（电影、电视剧）

    /// <summary>
    /// 是否为番剧season id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiSeasonId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ss");
    }

    /// <summary>
    /// 是否为番剧season url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiSeasonUrl(string input)
    {
        var id = GetBangumiId(input);
        return IsBangumiSeasonId(id);
    }

    /// <summary>
    /// 获取番剧season id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetBangumiSeasonId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiSeasonId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        if (IsBangumiSeasonUrl(input))
        {
            return Number.GetInt(GetBangumiId(input).Remove(0, 2));
        }

        return -1;
    }

    /// <summary>
    /// 是否为番剧episode id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiEpisodeId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ep");
    }

    /// <summary>
    /// 是否为番剧episode url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiEpisodeUrl(string input)
    {
        var id = GetBangumiId(input);
        return IsBangumiEpisodeId(id);
    }

    /// <summary>
    /// 获取番剧episode id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetBangumiEpisodeId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiEpisodeId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        if (IsBangumiEpisodeUrl(input))
        {
            return Number.GetInt(GetBangumiId(input).Remove(0, 2));
        }

        return -1;
    }

    /// <summary>
    /// 是否为番剧media id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiMediaId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "md");
    }

    /// <summary>
    /// 是否为番剧media url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsBangumiMediaUrl(string input)
    {
        var id = GetBangumiId(input);
        return IsBangumiMediaId(id);
    }

    /// <summary>
    /// 获取番剧media id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetBangumiMediaId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsBangumiMediaId(input))
        {
            return Number.GetInt(input.Remove(0, 2));
        }

        if (IsBangumiMediaUrl(input))
        {
            return Number.GetInt(GetBangumiId(input).Remove(0, 2));
        }

        return -1;
    }

    #endregion

    #region 课程

    /// <summary>
    /// 是否为课程season url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsCheeseSeasonUrl(string input)
    {
        string id = GetCheeseId(input);
        return IsIntId(id, "ss");
    }

    /// <summary>
    /// 获取课程season id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetCheeseSeasonId(string input)
    {
        return IsCheeseSeasonUrl(input) ? Number.GetInt(GetCheeseId(input).Remove(0, 2)) : -1;
    }

    /// <summary>
    /// 是否为课程episode url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsCheeseEpisodeUrl(string input)
    {
        string id = GetCheeseId(input);
        return IsIntId(id, "ep");
    }

    /// <summary>
    /// 获取课程episode id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static long GetCheeseEpisodeId(string input)
    {
        return IsCheeseEpisodeUrl(input) ? Number.GetInt(GetCheeseId(input).Remove(0, 2)) : -1;
    }

    #endregion

    #region 收藏夹

    /// <summary>
    /// 是否为收藏夹id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsFavoritesId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsIntId(input, "ml");
    }

    /// <summary>
    /// 是否为收藏夹url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsFavoritesUrl(string input)
    {
        return IsFavoritesUrl1(input) || IsFavoritesUrl2(input) || IsFavoritesUrl3(input);
    }

    /// <summary>
    /// 是否为收藏夹url1
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static bool IsFavoritesUrl1(string input)
    {
        var favoritesId = GetId(input, FavoritesUrl1);
        return IsFavoritesId(favoritesId);
    }

    /// <summary>
    /// 是否为收藏夹ur2
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static bool IsFavoritesUrl2(string input)
    {
        var favoritesId = GetId(input, FavoritesUrl2);
        return IsFavoritesId(favoritesId.Split('/')[0]);
    }

    /// <summary>
    /// 是否为收藏夹ur2
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static bool IsFavoritesUrl3(string input)
    {
        var favoritesId = GetId(input, FavoritesUrl3);
        return IsFavoritesId(favoritesId);
    }

    /// <summary>
    /// 获取收藏夹id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
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

        if (IsFavoritesUrl3(input))
        {
            return Number.GetInt(GetId(input, FavoritesUrl3).Remove(0, 2));
        }

        return -1;
    }

    #endregion

    #region UP主视频列表

    /// <summary>
    /// 是否为UP主全部视频列表URL。
    /// </summary>
    public static bool IsUserVideoListUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var id = GetId(input, FavoritesUrl3);
        return Regex.IsMatch(id, @"^\d+$");
    }

    /// <summary>
    /// 获取UP主全部视频列表中的用户mid。
    /// </summary>
    public static long GetUserVideoListId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsUserVideoListUrl(input)
            ? Number.GetInt(GetId(input, FavoritesUrl3))
            : -1;
    }

    #endregion

    #region 用户空间

    /// <summary>
    /// 是否为用户id
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsUserId(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(input.Remove(0, 4), @"^\d+$");
        }

        if (input.StartsWith("uid", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(input.Remove(0, 3), @"^\d+$");
        }

        return false;
    }

    /// <summary>
    /// 是否为用户空间url
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static bool IsUserUrl(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsUrl(input))
        {
            return false;
        }

        if (input.Contains("space.bilibili.com", StringComparison.Ordinal))
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// 获取用户mid
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
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

        if (IsUserUrl(input))
        {
            var url = EnableHttps(input);
            url = DeleteUrlParam(url);
            var match = Regex.Match(url, @"\d+");
            if (match.Success)
            {
                return long.Parse(match.Value, CultureInfo.InvariantCulture);
            }

            return -1;
        }

        return -1;
    }

    #endregion

    /// <summary>
    /// 是否为网址
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static bool IsUrl(string input)
    {
        return input.StartsWith("http://", StringComparison.Ordinal) || input.StartsWith("https://", StringComparison.Ordinal);
    }

    /// <summary>
    /// 将http转为https
    /// </summary>
    /// <returns></returns>
    private static string EnableHttps(string url)
    {
        if (!IsUrl(url))
        {
            return url;
        }

        return url.Replace("http://", "https://", StringComparison.Ordinal);
    }

    /// <summary>
    /// 去除url中的参数
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static string DeleteUrlParam(string url)
    {
        var strList = url.Split('?');

        return strList[0].EndsWith('/') ? strList[0].TrimEnd('/') : strList[0];
    }

    /// <summary>
    /// 从url中获取视频id（avid/bvid）
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static string GetVideoId(string input)
    {
        return GetId(input, VideoUrl);
    }

    /// <summary>
    /// 从url中获取番剧id（ss/ep/md）
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static string GetBangumiId(string input)
    {
        var id = GetId(input, BangumiUrl);
        return !string.IsNullOrEmpty(id) ? id : GetId(input, BangumiMediaUrl);
    }

    /// <summary>
    /// 从url中获取课程id（ss/ep）
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static string GetCheeseId(string input)
    {
        return GetId(input, CheeseUrl);
    }

    /// <summary>
    /// 是否为数字型id
    /// </summary>
    /// <param name="input"></param>
    /// <param name="prefix"></param>
    /// <returns></returns>
    private static bool IsIntId(string input, string prefix)
    {
        return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(input.Remove(0, 2), @"^\d+$");
    }

    /// <summary>
    /// 从url中获取id
    /// </summary>
    /// <param name="input"></param>
    /// <param name="baseUrl"></param>
    /// <returns></returns>
    private static string GetId(string input, string baseUrl)
    {
        if (!IsUrl(input))
        {
            return "";
        }

        var url = EnableHttps(input);
        url = DeleteUrlParam(url);

        url = url.Replace(ShareWwwUrl, WwwUrl, StringComparison.Ordinal);
        url = url.Replace(MobileUrl, WwwUrl, StringComparison.Ordinal);

        if (url.Contains("b23.tv/ss", StringComparison.Ordinal) || url.Contains("b23.tv/ep", StringComparison.Ordinal))
        {
            url = url.Replace(ShortUrl, BangumiUrl, StringComparison.Ordinal);
        }
        else
        {
            url = url.Replace(ShortUrl, VideoUrl, StringComparison.Ordinal);
        }

        return !url.StartsWith(baseUrl, StringComparison.Ordinal) ? "" : url.Replace(baseUrl, "", StringComparison.Ordinal);
    }
}
