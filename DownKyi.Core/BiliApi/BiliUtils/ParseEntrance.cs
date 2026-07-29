namespace DownKyi.Core.BiliApi.BiliUtils;

/// <summary>
/// Parses supported Bilibili video, bangumi, course, favorite, and user-space identifiers.
/// </summary>
public static partial class ParseEntrance
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
}
