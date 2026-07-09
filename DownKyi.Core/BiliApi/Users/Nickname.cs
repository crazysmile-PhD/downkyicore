using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户昵称
/// </summary>
public class Nickname
{
    /// <summary>
    /// 检查昵称
    /// </summary>
    /// <param name="nickName"></param>
    /// <returns></returns>
    public static NicknameStatus? CheckNickname(string nickName)
    {
        var url = $"https://api.bilibili.com/x/relation/stat?nickName={nickName}";
        const string referer = "https://www.bilibili.com";
        return BiliApiRequest.RequestJson<NicknameStatus>(
            url,
            referer,
            nameof(CheckNickname),
            "Nickname");
    }
}
