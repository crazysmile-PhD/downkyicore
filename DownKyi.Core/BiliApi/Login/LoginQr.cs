using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Login.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Login;

public static class LoginQr
{
    /// <summary>
    /// 申请二维码URL及扫码密钥（web端）
    /// </summary>
    /// <returns></returns>
    public static async Task<LoginUrlOrigin?> GetLoginUrlAsync(
        this IBilibiliApiClient client,
        CancellationToken cancellationToken = default)
    {
        const string getLoginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
        var response = await BiliApiRequest.RequestJsonAsync<LoginUrlOrigin>(
            client,
            getLoginUrl,
            null,
            nameof(GetLoginUrlAsync),
            nameof(LoginQr),
            includeCredentials: false,
            cancellationToken).ConfigureAwait(false);
        BiliApiRequest.RequirePayload(response.Data);
        return response;
    }

    /// <summary>
    /// 使用扫码登录（web端）
    /// </summary>
    /// <param name="qrcodeKey"></param>
    /// <returns></returns>
    public static async Task<LoginStatus?> GetLoginStatusAsync(
        this IBilibiliApiClient client,
        string qrcodeKey,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}";

        var response = await BiliApiRequest.RequestJsonAsync<LoginStatus>(
            client,
            url,
            null,
            nameof(GetLoginStatusAsync),
            nameof(LoginQr),
            includeCredentials: false,
            cancellationToken).ConfigureAwait(false);
        BiliApiRequest.RequirePayload(response.Data);
        return response;
    }
}
