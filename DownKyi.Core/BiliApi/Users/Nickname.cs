using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户昵称
/// </summary>
public static class Nickname
{
    /// <summary>
    /// 检查昵称
    /// </summary>
    /// <param name="nickName"></param>
    /// <returns></returns>
    public static Task<NicknameStatus> CheckNicknameAsync(
        this IBilibiliApiClient client,
        string nickName,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/relation/stat?nickName={nickName}";
        const string referer = "https://www.bilibili.com";
        return BiliApiRequest.RequestJsonAsync<NicknameStatus>(
            client,
            url,
            referer,
            nameof(CheckNicknameAsync),
            "Nickname",
            cancellationToken: cancellationToken);
    }
}
