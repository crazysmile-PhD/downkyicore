using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

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
    public static async Task<UserRelationStat?> GetUserRelationStatAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/stat?vmid={mid}";
        const string referer = "https://www.bilibili.com";
        var userRelationStat = await BiliApiRequest.RequestJsonAsync<UserRelationStatOrigin>(
            client,
            url,
            referer,
            nameof(GetUserRelationStatAsync),
            "UserStatus",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(userRelationStat.Data);
    }

    /// <summary>
    /// UP主状态数
    /// 
    /// 注：该接口需要任意用户登录，否则不会返回任何数据
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static async Task<UpStat?> GetUpStatAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/space/upstat?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var upStat = await BiliApiRequest.RequestJsonAsync<UpStatOrigin>(
            client,
            url,
            referer,
            nameof(GetUpStatAsync),
            "UserStatus",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(upStat.Data);
    }
}
