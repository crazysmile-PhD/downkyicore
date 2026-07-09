using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

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
    public static RelationFollow? GetFollowers(long mid, int pn, int ps)
    {
        var url = $"https://api.bilibili.com/x/relation/followers?vmid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationFollower = BiliApiRequest.RequestJson<RelationFollowOrigin>(
            url,
            referer,
            nameof(GetFollowers),
            "UserRelation");

        return relationFollower?.Data;
    }

    /// <summary>
    /// 查询用户所有的粉丝明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static List<RelationFollowInfo> GetAllFollowers(long mid)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = GetFollowers(mid, i, ps);
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
    public static RelationFollow? GetFollowings(long mid, int pn, int ps, FollowingOrder order = FollowingOrder.DEFAULT)
    {
        var orderType = "";
        if (order == FollowingOrder.ATTENTION)
        {
            orderType = "attention";
        }

        var url = $"https://api.bilibili.com/x/relation/followings?vmid={mid}&pn={pn}&ps={ps}&order_type={orderType}";
        const string referer = "https://www.bilibili.com";
        var relationFollower = BiliApiRequest.RequestJson<RelationFollowOrigin>(
            url,
            referer,
            nameof(GetFollowings),
            "UserRelation");

        return relationFollower?.Data;
    }

    /// <summary>
    /// 查询用户所有的关注明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static List<RelationFollowInfo> GetAllFollowings(long mid, FollowingOrder order = FollowingOrder.DEFAULT)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = GetFollowings(mid, i, ps, order);
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
    public static List<RelationFollowInfo>? GetWhispers(int pn, int ps)
    {
        var url = $"https://api.bilibili.com/x/relation/whispers?pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationWhisper = BiliApiRequest.RequestJson<RelationWhisper>(
            url,
            referer,
            nameof(GetWhispers),
            "UserRelation");

        return relationWhisper?.Data?.List;
    }

    /// <summary>
    /// 查询黑名单明细
    /// </summary>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static List<RelationFollowInfo>? GetBlacks(int pn, int ps)
    {
        var url = $"https://api.bilibili.com/x/relation/blacks?pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var relationBlack = BiliApiRequest.RequestJson<RelationBlack>(
            url,
            referer,
            nameof(GetBlacks),
            "UserRelation");

        return relationBlack?.Data;
    }

    #region 关注分组相关，只能查询当前登录账户的信息

    /// <summary>
    /// 查询关注分组列表
    /// </summary>
    /// <returns></returns>
    public static List<FollowingGroup>? GetFollowingGroup()
    {
        const string url = $"https://api.bilibili.com/x/relation/tags";
        const string referer = "https://www.bilibili.com";
        var followingGroup = BiliApiRequest.RequestJson<FollowingGroupOrigin>(
            url,
            referer,
            nameof(GetFollowingGroup),
            "UserRelation");

        return followingGroup?.Data;
    }

    /// <summary>
    /// 查询关注分组明细
    /// </summary>
    /// <param name="tagId">分组ID</param>
    /// <param name="pn">页数</param>
    /// <param name="ps">每页项数</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static List<RelationFollowInfo>? GetFollowingGroupContent(long tagId, int pn, int ps,
        FollowingOrder order = FollowingOrder.DEFAULT)
    {
        var orderType = "";
        if (order == FollowingOrder.ATTENTION)
        {
            orderType = "attention";
        }

        var url =
            $"https://api.bilibili.com/x/relation/tag?tagid={tagId}&pn={pn}&ps={ps}&order_type={orderType}";
        const string referer = "https://www.bilibili.com";
        var content = BiliApiRequest.RequestJson<FollowingGroupContent>(
            url,
            referer,
            nameof(GetFollowingGroupContent),
            "UserRelation");

        return content?.Data;
    }

    /// <summary>
    /// 查询所有的关注分组明细
    /// </summary>
    /// <param name="tagId">分组ID</param>
    /// <param name="order">排序方式</param>
    /// <returns></returns>
    public static List<RelationFollowInfo> GetAllFollowingGroupContent(int tagId,
        FollowingOrder order = FollowingOrder.DEFAULT)
    {
        var result = new List<RelationFollowInfo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = GetFollowingGroupContent(tagId, i, ps, order);
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
