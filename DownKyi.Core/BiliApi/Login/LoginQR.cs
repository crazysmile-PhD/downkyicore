using Avalonia.Media.Imaging;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Utils;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Login;

public static class LoginQr
{
    /// <summary>
    /// 申请二维码URL及扫码密钥（web端）
    /// </summary>
    /// <returns></returns>
    public static LoginUrlOrigin? GetLoginUrl()
    {
        const string getLoginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
        var response = BiliApiRequest.RequestJson<LoginUrlOrigin>(
            getLoginUrl,
            null,
            nameof(GetLoginUrl),
            "LoginQR");
        BiliApiRequest.RequirePayload(response.Data);
        return response;
    }

    /// <summary>
    /// 使用扫码登录（web端）
    /// </summary>
    /// <param name="qrcodeKey"></param>
    /// <returns></returns>
    public static LoginStatus? GetLoginStatus(string qrcodeKey)
    {
        var url = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}";

        var response = BiliApiRequest.RequestJson<LoginStatus>(
            url,
            null,
            nameof(GetLoginStatus),
            "LoginQR");
        BiliApiRequest.RequirePayload(response.Data);
        return response;
    }

    /// <summary>
    /// 获得登录二维码
    /// </summary>
    /// <returns></returns>
    public static Bitmap? GetLoginQrCode()
    {
        try
        {
            var loginAddress = GetLoginUrl()?.Data?.QrCodeAddress;
            return Uri.TryCreate(loginAddress, UriKind.Absolute, out var loginUri)
                ? GetLoginQrCode(loginUri)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 根据输入url生成二维码
    /// </summary>
    /// <param name="loginUri"></param>
    /// <returns></returns>
    public static Bitmap? GetLoginQrCode(Uri? loginUri)
    {
        if (loginUri == null) return null;
        // 设置的参数影响app能否成功扫码
        var qrCode = QrCode.EncodeQrCode(loginUri.AbsoluteUri, 11, 10, null, 0, 0, false);

        return qrCode;
    }
}
