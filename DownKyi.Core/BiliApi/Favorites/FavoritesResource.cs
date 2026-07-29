using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Favorites.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites;

public static class FavoritesResource
{
    /// <summary>
    /// 获取收藏夹内容明细列表
    /// </summary>
    /// <param name="mediaId">收藏夹ID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMedia>?> GetFavoritesMediaAsync(
        this IBilibiliApiClient client,
        long mediaId,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var resource = await client.GetFavoritesMediaResourceAsync(
            mediaId,
            pn,
            ps,
            null,
            cancellationToken).ConfigureAwait(false);
        return resource.Medias;
    }

    /// <summary>
    /// 获取收藏夹内容和服务端的后续分页标记。
    /// </summary>
    public static async Task<FavoritesMediaResource> GetFavoritesMediaResourceAsync(
        this IBilibiliApiClient client,
        long mediaId,
        int pn,
        int ps,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var url = BuildFavoritesMediaUrl(mediaId, pn, ps, keyword);
        const string referer = "https://www.bilibili.com";
        var resource = await BiliApiRequest.RequestJsonAsync<FavoritesMediaResourceOrigin>(
            client,
            url,
            referer,
            nameof(GetFavoritesMediaAsync),
            "FavoritesResource",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(resource.Data);
    }

    internal static string BuildFavoritesMediaUrl(long mediaId, int pn, int ps, string? keyword)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/resource/list?media_id={mediaId}&pn={pn}&ps={ps}&platform=web";
        var normalizedKeyword = keyword?.Trim();
        return string.IsNullOrEmpty(normalizedKeyword)
            ? url
            : $"{url}&keyword={Uri.EscapeDataString(normalizedKeyword)}";
    }

    /// <summary>
    /// 获取收藏夹内容明细列表（全部）
    /// </summary>
    /// <param name="mediaId">收藏夹ID</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMedia>> GetAllFavoritesMediaAsync(
        this IBilibiliApiClient client,
        long mediaId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<FavoritesMedia>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 20;

            cancellationToken.ThrowIfCancellationRequested();
            var data = await client.GetFavoritesMediaAsync(mediaId, i, ps, cancellationToken)
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
    /// 获取收藏夹全部内容id
    /// </summary>
    /// <param name="mediaId"></param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FavoritesMediaId>?> GetFavoritesMediaIdAsync(
        this IBilibiliApiClient client,
        long mediaId,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v3/fav/resource/ids?media_id={mediaId}";
        const string referer = "https://www.bilibili.com";
        var media = await BiliApiRequest.RequestJsonAsync<FavoritesMediaIdOrigin>(
            client,
            url,
            referer,
            nameof(GetFavoritesMediaIdAsync),
            "FavoritesResource",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(media.Data);
    }
}
