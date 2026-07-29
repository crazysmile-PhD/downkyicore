using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Storage;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户基本信息
/// </summary>
public static class UserInfo
{
    private const int AnonymousNavigationCode = -101;

    /// <summary>
    /// 导航栏用户信息
    /// </summary>
    /// <returns></returns>
    public static async Task<UserInfoForNavigation?> GetUserInfoForNavigationAsync(
        this IBilibiliApiClient client,
        CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/web-interface/nav";
        const string referer = "https://www.bilibili.com";
        // The nav endpoint returns -101 for anonymous users while still supplying
        // the public WBI metadata required to sign ordinary video requests.
        var userInfo = await BiliApiRequest.RequestJsonAllowingCodeAsync<UserInfoForNavigationOrigin>(
            client,
            url,
            referer,
            nameof(GetUserInfoForNavigationAsync),
            "UserInfo",
            AnonymousNavigationCode,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(userInfo.Data);
    }

    /// <summary>
    /// 用户空间详细信息
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static async Task<UserInfoForSpace?> GetUserInfoForSpaceAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long mid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var parameters = new Dictionary<string, object?>
        {
            { "mid", mid }
        };

        if (!File.Exists(ApplicationStorage.GetLogin()))
        {
            parameters.Add("dm_img_str", "V2ViR0wgMS");
            parameters.Add("dm_img_list", "[]");
            parameters.Add("dm_cover_img_str", "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgR1RYIDk4MCBEaXJlY3QzRDExIHZzXzVfMCBwc181XzApLCBvciBzaW1pbGFyR29vZ2xlIEluYy4gKE5WSURJQS");
            parameters.Add("dm_img_inter", "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}");
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/space/wbi/acc/info?{query}";
        const string referer = "https://www.bilibili.com";
        var spaceInfo = await BiliApiRequest.RequestJsonAsync<UserInfoForSpaceOrigin>(
            client,
            url,
            referer,
            nameof(GetUserInfoForSpaceAsync),
            "UserInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(spaceInfo.Data);
    }

    /// <summary>
    /// 本用户详细信息
    /// </summary>
    /// <returns></returns>
    public static async Task<MyInfo?> GetMyInfoAsync(
        this IBilibiliApiClient client,
        CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/space/myinfo";
        const string referer = "https://www.bilibili.com";
        var myInfo = await BiliApiRequest.RequestJsonAsync<MyInfoOrigin>(
            client,
            url,
            referer,
            nameof(GetMyInfoAsync),
            "UserInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(myInfo.Data);
    }
}
