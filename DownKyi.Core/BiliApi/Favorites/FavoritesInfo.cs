using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites;

public static class FavoritesInfo
{
    /// <summary>
    /// 获取收藏夹元数据
    /// </summary>
    /// <param name="mediaId"></param>
    public static FavoritesMetaInfo? GetFavoritesInfo(long mediaId, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/info?media_id={mediaId}";
        const string referer = "https://www.bilibili.com";
        var info = BiliApiRequest.RequestJson<FavoritesMetaInfoOrigin>(
            url,
            referer,
            nameof(GetFavoritesInfo),
            "FavoritesInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(info.Data);
    }

    /// <summary>
    /// 查询用户创建的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static IReadOnlyList<FavoritesMetaInfo>? GetCreatedFavorites(long mid, int pn, int ps, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/created/list?up_mid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var favorites = BiliApiRequest.RequestJson<FavoritesListOrigin>(
            url,
            referer,
            nameof(GetCreatedFavorites),
            "FavoritesInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(favorites.Data).List;
    }

    /// <summary>
    /// 查询所有的用户创建的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static IReadOnlyList<FavoritesMetaInfo> GetAllCreatedFavorites(long mid, CancellationToken cancellationToken = default)
    {
        var result = new List<FavoritesMetaInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            cancellationToken.ThrowIfCancellationRequested();
            var data = GetCreatedFavorites(mid, i, ps, cancellationToken);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }

    /// <summary>
    /// 查询用户收藏的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static IReadOnlyList<FavoritesMetaInfo>? GetCollectedFavorites(long mid, int pn, int ps, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var favorites = BiliApiRequest.RequestJson<FavoritesListOrigin>(
            url,
            referer,
            nameof(GetCollectedFavorites),
            "FavoritesInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(favorites.Data).List;
    }

    /// <summary>
    /// 查询所有的用户收藏的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static IReadOnlyList<FavoritesMetaInfo> GetAllCollectedFavorites(long mid, CancellationToken cancellationToken = default)
    {
        var result = new List<FavoritesMetaInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            cancellationToken.ThrowIfCancellationRequested();
            var data = GetCollectedFavorites(mid, i, ps, cancellationToken);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }
}
