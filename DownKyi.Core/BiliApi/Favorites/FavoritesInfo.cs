using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Favorites.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites;

public static class FavoritesInfo
{
    /// <summary>
    /// 获取收藏夹元数据
    /// </summary>
    /// <param name="mediaId"></param>
    public static async Task<FavoritesMetaInfo?> GetFavoritesInfoAsync(
        this IBilibiliApiClient client,
        long mediaId,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/info?media_id={mediaId}";
        const string referer = "https://www.bilibili.com";
        var info = await BiliApiRequest.RequestJsonAsync<FavoritesMetaInfoOrigin>(
            client,
            url,
            referer,
            nameof(GetFavoritesInfoAsync),
            "FavoritesInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(info.Data);
    }

    /// <summary>
    /// 查询用户创建的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMetaInfo>?> GetCreatedFavoritesAsync(
        this IBilibiliApiClient client,
        long mid,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/created/list?up_mid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var favorites = await BiliApiRequest.RequestJsonAsync<FavoritesListOrigin>(
            client,
            url,
            referer,
            nameof(GetCreatedFavoritesAsync),
            "FavoritesInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(favorites.Data).List;
    }

    /// <summary>
    /// 查询所有的用户创建的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMetaInfo>> GetAllCreatedFavoritesAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var result = new List<FavoritesMetaInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            cancellationToken.ThrowIfCancellationRequested();
            var data = await client.GetCreatedFavoritesAsync(mid, i, ps, cancellationToken)
                .ConfigureAwait(false);
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
    public static async Task<IReadOnlyList<FavoritesMetaInfo>?> GetCollectedFavoritesAsync(
        this IBilibiliApiClient client,
        long mid,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var favorites = await BiliApiRequest.RequestJsonAsync<FavoritesListOrigin>(
            client,
            url,
            referer,
            nameof(GetCollectedFavoritesAsync),
            "FavoritesInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(favorites.Data).List;
    }

    /// <summary>
    /// 查询所有的用户收藏的视频收藏夹
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMetaInfo>> GetAllCollectedFavoritesAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var result = new List<FavoritesMetaInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            cancellationToken.ThrowIfCancellationRequested();
            var data = await client.GetCollectedFavoritesAsync(mid, i, ps, cancellationToken)
                .ConfigureAwait(false);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }
}
