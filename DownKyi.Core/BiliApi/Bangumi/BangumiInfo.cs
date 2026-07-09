using DownKyi.Core.BiliApi.Bangumi.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.Bangumi;

public static class BangumiInfo
{
    /// <summary>
    /// 剧集基本信息（mediaId方式）
    /// </summary>
    /// <param name="mediaId"></param>
    /// <returns></returns>
    public static BangumiMedia? BangumiMediaInfo(long mediaId, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/pgc/review/user?media_id={mediaId}";
        const string referer = "https://www.bilibili.com";
        var media = BiliApiRequest.RequestJson<BangumiMediaOrigin>(
            url,
            referer,
            nameof(BangumiMediaInfo),
            "BangumiInfo",
            cancellationToken);

        return media?.Result?.Media;
    }

    /// <summary>
    /// 获取剧集明细（web端）（seasonId/episodeId方式）
    /// </summary>
    /// <param name="seasonId"></param>
    /// <param name="episodeId"></param>
    /// <returns></returns>
    public static BangumiSeason? BangumiSeasonInfo(long seasonId = -1, long episodeId = -1, CancellationToken cancellationToken = default)
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

        var bangumiSeason = BiliApiRequest.RequestJson<BangumiSeasonOrigin>(
            url,
            referer,
            nameof(BangumiSeasonInfo),
            "BangumiInfo",
            cancellationToken);

        return bangumiSeason?.Result;
    }
}
