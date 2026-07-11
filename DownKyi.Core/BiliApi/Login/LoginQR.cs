using Avalonia.Media.Imaging;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Logging;
using DownKyi.Core.Utils;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

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
        return BiliApiRequest.RequestJson<LoginUrlOrigin>(
            getLoginUrl,
            null,
            nameof(GetLoginUrl),
            "LoginQR");
    }

    /// <summary>
    /// 使用扫码登录（web端）
    /// </summary>
    /// <param name="qrcodeKey"></param>
    /// <returns></returns>
    public static LoginStatus? GetLoginStatus(string qrcodeKey)
    {
        var url = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}";

        return BiliApiRequest.RequestJson<LoginStatus>(
            url,
            null,
            nameof(GetLoginStatus),
            "LoginQR");
    }

    /// <summary>
    /// 获得登录二维码
    /// </summary>
    /// <returns></returns>
    public static Bitmap? GetLoginQrCode()
    {
        try
        {
            var loginUrl = GetLoginUrl()?.Data?.Url;
            return GetLoginQrCode(loginUrl);
        }
        catch (ArgumentException e)
        {
            Console.PrintLine("GetLoginQrCode()发生异常: {0}", e);
            LogManager.Error("LoginQR", e);
            return null;
        }
        catch (InvalidOperationException e)
        {
            Console.PrintLine("GetLoginQrCode()状态无效: {0}", e);
            LogManager.Error("LoginQR", e);
            return null;
        }
    }

    /// <summary>
    /// 根据输入url生成二维码
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public static Bitmap? GetLoginQrCode(string? url)
    {
        if (url == null) return null;
        // 设置的参数影响app能否成功扫码
        var qrCode = QrCode.EncodeQrCode(url, 11, 10, null, 0, 0, false);

        return qrCode;
    }
}
