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
            var url = $"https://api.bilibili.com/x/v3/fav/folder/created/list?up_mid={mid}&pn={i}&ps={ps}";
            const string referer = "https://www.bilibili.com";
            var favorites = await BiliApiRequest.RequestJsonAsync<FavoritesListOrigin>(
                client,
                url,
                referer,
                nameof(GetAllCreatedFavoritesAsync),
                "FavoritesInfo",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = BiliApiRequest.RequirePayload(favorites.Data);
            if (data.List.Count == 0)
            {
                break;
            }

            result.AddRange(data.List);
            if (data.Count > 0 && result.Count >= data.Count)
            {
                break;
            }
        }

        return result;
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
            var url = $"https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid={mid}&pn={i}&ps={ps}";
            const string referer = "https://www.bilibili.com";
            var favorites = await BiliApiRequest.RequestJsonAsync<FavoritesListOrigin>(
                client,
                url,
                referer,
                nameof(GetAllCollectedFavoritesAsync),
                "FavoritesInfo",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = BiliApiRequest.RequirePayload(favorites.Data);
            if (data.List.Count == 0)
            {
                break;
            }

            result.AddRange(data.List);
            if (data.Count > 0 && result.Count >= data.Count)
            {
                break;
            }
        }

        return result;
    }
}
