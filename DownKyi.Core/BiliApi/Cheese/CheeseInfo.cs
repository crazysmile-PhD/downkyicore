using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Cheese.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese;

public static class CheeseInfo
{
    /// <summary>
    /// 获取课程基本信息
    /// </summary>
    /// <param name="seasonId"></param>
    /// <param name="episodeId"></param>
    /// <returns></returns>
    public static async Task<CheeseView?> CheeseViewInfoAsync(
        this IBilibiliApiClient client,
        long seasonId = -1,
        long episodeId = -1,
        CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/pugv/view/web/season";
        const string referer = "https://www.bilibili.com";
        string url;
        if (seasonId > -1)
        {
            url = $"{baseUrl}?season_id={seasonId}";
        }
        else if (episodeId > -1)
        {
            url = $"{baseUrl}?ep_id={episodeId}";
        }
        else
        {
            return null;
        }

        var cheese = await BiliApiRequest.RequestJsonAsync<CheeseViewOrigin>(
            client,
            url,
            referer,
            nameof(CheeseViewInfoAsync),
            "CheeseInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(cheese.Data);
    }

    /// <summary>
    /// 获取课程分集列表
    /// </summary>
    /// <param name="seasonId"></param>
    /// <param name="ps"></param>
    /// <param name="pn"></param>
    /// <returns></returns>
    public static async Task<CheeseEpisodeList?> CheeseEpisodeListAsync(
        this IBilibiliApiClient client,
        long seasonId,
        int ps = 50,
        int pn = 1,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/pugv/view/web/ep/list?season_id={seasonId}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var cheese = await BiliApiRequest.RequestJsonAsync<CheeseEpisodeListOrigin>(
            client,
            url,
            referer,
            nameof(CheeseEpisodeListAsync),
            "CheeseInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(cheese.Data);
    }
}
