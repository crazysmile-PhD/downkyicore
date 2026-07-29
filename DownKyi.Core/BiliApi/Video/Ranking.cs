using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Video.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video;

public static class Ranking
{
    /// <summary>
    /// 获取分区视频排行榜列表
    /// </summary>
    /// <param name="rid">目标分区tid</param>
    /// <param name="day">3日榜或周榜（3/7）</param>
    /// <param name="original"></param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RankingVideoView>?> RegionRankingListAsync(
        this IBilibiliApiClient client,
        int rid,
        int day = 3,
        int original = 0,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/web-interface/ranking/region?rid={rid}&day={day}&ps={original}";
        const string referer = "https://www.bilibili.com";
        var ranking = await BiliApiRequest.RequestJsonAsync<RegionRanking>(
            client,
            url,
            referer,
            nameof(RegionRankingListAsync),
            "Ranking",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(ranking.Data);
    }
}
