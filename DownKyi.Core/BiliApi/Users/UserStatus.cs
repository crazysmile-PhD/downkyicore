using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户状态数
/// </summary>
public static class UserStatus
{
    /// <summary>
    /// 关系状态数
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static UserRelationStat? GetUserRelationStat(long mid, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/stat?vmid={mid}";
        const string referer = "https://www.bilibili.com";
        var userRelationStat = BiliApiRequest.RequestJson<UserRelationStatOrigin>(
            url,
            referer,
            nameof(GetUserRelationStat),
            "UserStatus",
            cancellationToken);

        return userRelationStat?.Data;
    }

    /// <summary>
    /// UP主状态数
    /// 
    /// 注：该接口需要任意用户登录，否则不会返回任何数据
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static UpStat? GetUpStat(long mid)
    {
        var url = $"https://api.bilibili.com/x/space/upstat?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var upStat = BiliApiRequest.RequestJson<UpStatOrigin>(
            url,
            referer,
            nameof(GetUpStat),
            "UserStatus");

        return upStat?.Data;
    }
}
