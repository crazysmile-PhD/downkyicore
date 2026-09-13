namespace DownKyi.Application.Bilibili;

public sealed record BilibiliLoginResponse(
    string Content,
    IReadOnlyList<BilibiliLoginCookie> Cookies);
