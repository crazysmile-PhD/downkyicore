using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Users.Models;

namespace DownKyi.Core.BiliApi.Users;

public static partial class UserSpace
{
    public static async Task<SpaceSeasonsSeries?> GetSeasonsSeriesAsync(
        this IBilibiliApiClient client,
        long mid,
        int pageNum,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/polymer/web-space/seasons_series_list?mid={mid}&page_num={pageNum}&page_size={pageSize}";
        const string referer = "https://www.bilibili.com";
        var origin = await BiliApiRequest.RequestJsonAsync<SpaceSeasonsSeriesOrigin>(
            client,
            url,
            referer,
            nameof(GetSeasonsSeriesAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(origin.Data).ItemsLists;
    }

    public static async Task<SpaceSeasonsDetail?> GetSeasonsDetailAsync(
        this IBilibiliApiClient client,
        long mid,
        long seasonId,
        int pageNum,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid={mid}&season_id={seasonId}&page_num={pageNum}&page_size={pageSize}&sort_reverse=false";
        const string referer = "https://www.bilibili.com";
        var origin = await BiliApiRequest.RequestJsonAsync<SpaceSeasonsDetailOrigin>(
            client,
            url,
            referer,
            nameof(GetSeasonsDetailAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(origin.Data);
    }

    public static async Task<SpaceSeriesMetaData?> GetSeriesMetaAsync(
        this IBilibiliApiClient client,
        long seriesId,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/series/series?series_id={seriesId}";
        const string referer = "https://www.bilibili.com";
        var origin = await BiliApiRequest.RequestJsonAsync<SpaceSeriesMetaOrigin>(
            client,
            url,
            referer,
            nameof(GetSeriesMetaAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(origin.Data);
    }

    public static async Task<SpaceSeriesDetail?> GetSeriesDetailAsync(
        this IBilibiliApiClient client,
        long mid,
        long seriesId,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/series/archives?mid={mid}&series_id={seriesId}&only_normal=true&sort=desc&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var origin = await BiliApiRequest.RequestJsonAsync<SpaceSeriesDetailOrigin>(
            client,
            url,
            referer,
            nameof(GetSeriesDetailAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(origin.Data);
    }

    public static async Task<BangumiFollowData?> GetBangumiFollowAsync(
        this IBilibiliApiClient client,
        long mid,
        BangumiType type,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/space/bangumi/follow/list?vmid={mid}&type={type:D}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var bangumiFollow = await BiliApiRequest.RequestJsonAsync<BangumiFollowOrigin>(
            client,
            url,
            referer,
            nameof(GetBangumiFollowAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(bangumiFollow.Data);
    }

}
