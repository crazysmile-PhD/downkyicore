using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Bangumi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Bangumi;

public static class BangumiInfo
{
    /// <summary>
    /// 剧集基本信息（mediaId方式）
    /// </summary>
    /// <param name="mediaId"></param>
    /// <returns></returns>
    public static async Task<BangumiMedia?> BangumiMediaInfoAsync(
        this IBilibiliApiClient client,
        long mediaId,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/pgc/review/user?media_id={mediaId}";
        const string referer = "https://www.bilibili.com";
        var media = await BiliApiRequest.RequestJsonAsync<BangumiMediaOrigin>(
            client,
            url,
            referer,
            nameof(BangumiMediaInfoAsync),
            "BangumiInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(media.Result, "result").Media;
    }

    /// <summary>
    /// 获取剧集明细（web端）（seasonId/episodeId方式）
    /// </summary>
    /// <param name="seasonId"></param>
    /// <param name="episodeId"></param>
    /// <returns></returns>
    public static async Task<BangumiSeason?> BangumiSeasonInfoAsync(
        this IBilibiliApiClient client,
        long seasonId = -1,
        long episodeId = -1,
        CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/pgc/view/web/season";
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

        var bangumiSeason = await BiliApiRequest.RequestJsonAsync<BangumiSeasonOrigin>(
            client,
            url,
            referer,
            nameof(BangumiSeasonInfoAsync),
            "BangumiInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(bangumiSeason.Result, "result");
    }
}
