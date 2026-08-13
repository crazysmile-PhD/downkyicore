using DownKyi.Application.Bilibili;

namespace DownKyi.Core.BiliApi.Login.Models;

public sealed record LoginStatusResult(
    LoginStatus Status,
    IReadOnlyList<BilibiliLoginCookie> Cookies);
