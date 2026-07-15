using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户基本信息
/// </summary>
public static class UserInfo
{
    /// <summary>
    /// 导航栏用户信息
    /// </summary>
    /// <returns></returns>
    public static UserInfoForNavigation? GetUserInfoForNavigation(CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/web-interface/nav";
        const string referer = "https://www.bilibili.com";
        var userInfo = BiliApiRequest.RequestJson<UserInfoForNavigationOrigin>(
            url,
            referer,
            nameof(GetUserInfoForNavigation),
            "UserInfo",
            cancellationToken);

        return userInfo?.Data;
    }

    /// <summary>
    /// 用户空间详细信息
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static UserInfoForSpace? GetUserInfoForSpace(ISettingsStore settingsStore, long mid)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        var parameters = new Dictionary<string, object?>
        {
            { "mid", mid }
        };

        if (!File.Exists(StorageManager.GetLogin()))
        {
            parameters.Add("dm_img_str", "V2ViR0wgMS");
            parameters.Add("dm_img_list", "[]");
            parameters.Add("dm_cover_img_str", "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgR1RYIDk4MCBEaXJlY3QzRDExIHZzXzVfMCBwc181XzApLCBvciBzaW1pbGFyR29vZ2xlIEluYy4gKE5WSURJQS");
            parameters.Add("dm_img_inter", "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}");
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(parameters, settingsStore));
        var url = $"https://api.bilibili.com/x/space/wbi/acc/info?{query}";
        const string referer = "https://www.bilibili.com";
        var spaceInfo = BiliApiRequest.RequestJson<UserInfoForSpaceOrigin>(
            url,
            referer,
            nameof(GetUserInfoForSpace),
            "UserInfo");

        return spaceInfo?.Data;
    }

    /// <summary>
    /// 本用户详细信息
    /// </summary>
    /// <returns></returns>
    public static MyInfo? GetMyInfo(CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/space/myinfo";
        const string referer = "https://www.bilibili.com";
        var myInfo = BiliApiRequest.RequestJson<MyInfoOrigin>(
            url,
            referer,
            nameof(GetMyInfo),
            "UserInfo",
            cancellationToken);

        return myInfo?.Data;
    }
}
