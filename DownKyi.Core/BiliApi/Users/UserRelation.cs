using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户关系相关
/// </summary>
public static class UserRelation
{
    /// <summary>
    /// 查询用户粉丝明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static async Task<RelationFollow?> GetFollowersAsync(
        this IBilibiliApiClient client,
        long mid,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/followers?vmid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationFollower = await BiliApiRequest.RequestJsonAsync<RelationFollowOrigin>(
            client,
            url,
            referer,
            nameof(GetFollowersAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(relationFollower.Data);
    }

    /// <summary>
    /// 查询用户所有的粉丝明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>> GetAllFollowersAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = await client.GetFollowersAsync(mid, i, ps, cancellationToken)
                .ConfigureAwait(false);
            if (data == null || data.List == null || data.List.Count == 0)
            {
                break;
            }

            result.AddRange(data.List);
        }

        return result;
    }

    /// <summary>
    /// 查询用户关注明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static async Task<RelationFollow?> GetFollowingsAsync(
        this IBilibiliApiClient client,
        long mid,
        int pn,
        int ps,
        FollowingOrder order = FollowingOrder.DEFAULT,
        CancellationToken cancellationToken = default)
    {
        var orderType = "";
        if (order == FollowingOrder.ATTENTION)
        {
            orderType = "attention";
        }

        var url = $"https://api.bilibili.com/x/relation/followings?vmid={mid}&pn={pn}&ps={ps}&order_type={orderType}";
        const string referer = "https://www.bilibili.com";
        var relationFollower = await BiliApiRequest.RequestJsonAsync<RelationFollowOrigin>(
            client,
            url,
            referer,
            nameof(GetFollowingsAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(relationFollower.Data);
    }

    /// <summary>
    /// 查询用户所有的关注明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>> GetAllFollowingsAsync(
        this IBilibiliApiClient client,
        long mid,
        FollowingOrder order = FollowingOrder.DEFAULT,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = await client.GetFollowingsAsync(mid, i, ps, order, cancellationToken)
                .ConfigureAwait(false);
            if (data == null || data.List == null || data.List.Count == 0)
            {
                break;
            }

            result.AddRange(data.List);
        }

        return result;
    }

    /// <summary>
    /// 查询悄悄关注明细
    /// </summary>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>?> GetWhispersAsync(
        this IBilibiliApiClient client,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/whispers?pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationWhisper = await BiliApiRequest.RequestJsonAsync<RelationWhisper>(
            client,
            url,
            referer,
            nameof(GetWhispersAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(relationWhisper.Data).List;
    }

    /// <summary>
    /// 查询黑名单明细
    /// </summary>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>?> GetBlacksAsync(
        this IBilibiliApiClient client,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/blacks?pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationBlack = await BiliApiRequest.RequestJsonAsync<RelationBlack>(
            client,
            url,
            referer,
            nameof(GetBlacksAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(relationBlack.Data);
    }

    #region 关注分组相关，只能查询当前登录账户的信息

    /// <summary>
    /// 查询关注分组列表
    /// </summary>
    /// <returns></returns>
    public static async Task<IReadOnlyList<FollowingGroup>?> GetFollowingGroupAsync(
        this IBilibiliApiClient client,
        CancellationToken cancellationToken = default)
    {
        const string url = $"https://api.bilibili.com/x/relation/tags";
        const string referer = "https://www.bilibili.com";
        var followingGroup = await BiliApiRequest.RequestJsonAsync<FollowingGroupOrigin>(
            client,
            url,
            referer,
            nameof(GetFollowingGroupAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(followingGroup.Data);
    }

    /// <summary>
    /// 查询关注分组明细
    /// </summary>
    /// <param name="tagId">分组ID</param>
    /// <param name="pn">页数</param>
    /// <param name="ps">每页项数</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>?> GetFollowingGroupContentAsync(
        this IBilibiliApiClient client,
        long tagId,
        int pn,
        int ps,
        FollowingOrder order = FollowingOrder.DEFAULT,
        CancellationToken cancellationToken = default)
    {
        var orderType = "";
        if (order == FollowingOrder.ATTENTION)
        {
            orderType = "attention";
        }

        var url =
            $"https://api.bilibili.com/x/relation/tag?tagid={tagId}&pn={pn}&ps={ps}&order_type={orderType}";
        const string referer = "https://www.bilibili.com";
        var content = await BiliApiRequest.RequestJsonAsync<FollowingGroupContent>(
            client,
            url,
            referer,
            nameof(GetFollowingGroupContentAsync),
            "UserRelation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(content.Data);
    }

    /// <summary>
    /// 查询所有的关注分组明细
    /// </summary>
    /// <param name="tagId">分组ID</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<RelationFollowInfo>> GetAllFollowingGroupContentAsync(
        this IBilibiliApiClient client,
        int tagId,
        FollowingOrder order = FollowingOrder.DEFAULT,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = await client.GetFollowingGroupContentAsync(
                tagId,
                i,
                ps,
                order,
                cancellationToken).ConfigureAwait(false);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }

    #endregion
}
