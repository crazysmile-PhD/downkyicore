using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Favorites.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites;

public static class FavoritesResource
{
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
            nameof(GetFavoritesMediaResourceAsync),
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
            var page = await client.GetFavoritesMediaResourceAsync(
                mediaId,
                i,
                ps,
                null,
                cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Medias);
            if (!page.HasMore)
            {
                break;
            }
        }

        return result;
    }
}
