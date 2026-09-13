using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Login.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Login;

public static class LoginQr
{
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
    /// 申请二维码URL及扫码密钥（web端）
    /// </summary>
    /// <returns></returns>
    public static async Task<LoginUrlOrigin?> GetLoginUrlAsync(
        this IBilibiliLoginSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        const string getLoginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
        var httpResponse = await session.GetAsync(
            new BilibiliHttpRequest(
                getLoginUrl,
                includeCredentials: false),
            cancellationToken).ConfigureAwait(false);
        var response = BiliApiRequest.ParseJson<LoginUrlOrigin>(
            httpResponse.Content,
            nameof(GetLoginUrlAsync));
        BiliApiRequest.RequirePayload(response.Data);
        return response;
    }

    /// <summary>
    /// 使用扫码登录（web端）
    /// </summary>
    /// <param name="qrcodeKey"></param>
    /// <returns></returns>
    public static async Task<LoginStatusResult> GetLoginStatusAsync(
        this IBilibiliLoginSession session,
        string qrcodeKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(qrcodeKey);
        var url = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}";

        var httpResponse = await session.GetAsync(
            new BilibiliHttpRequest(
                url,
                includeCredentials: false),
            cancellationToken).ConfigureAwait(false);
        var response = BiliApiRequest.ParseJson<LoginStatus>(
            httpResponse.Content,
            nameof(GetLoginStatusAsync));
        BiliApiRequest.RequirePayload(response.Data);
        return new LoginStatusResult(response, httpResponse.Cookies);
    }

    public static async Task<LoginStatus?> GetLoginStatusAsync(
        this IBilibiliApiClient client,
        string qrcodeKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(qrcodeKey);
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
