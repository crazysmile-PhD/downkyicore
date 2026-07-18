using Avalonia.Media.Imaging;
using QRCoder;

namespace DownKyi.Core.Utils;

public static class QrCode
{
    /// <summary>
    /// 生成二维码
    /// </summary>
    /// <param name="msg">信息</param>
    /// <param name="version">版本 1 ~ 40</param>
    /// <param name="pixel">像素点大小</param>
    /// <param name="iconPath">图标路径</param>
    /// <param name="iconSize">图标尺寸</param>
    /// <param name="iconBorder">图标边框厚度</param>
    /// <param name="whiteEdge">二维码白边</param>
    /// <returns>位图</returns>
    public static Bitmap EncodeQrCode(string msg, int version, int pixel, string? iconPath, int iconSize, int iconBorder, bool whiteEdge)
    {
        using var codeGenerator = new QRCodeGenerator();

        using var codeData = codeGenerator.CreateQrCode(msg, QRCodeGenerator.ECCLevel.H /* 这里设置容错率的一个级别 */, true, false, QRCodeGenerator.EciMode.Utf8, version);

        using var qrCode = new BitmapByteQRCode(codeData);
        var qrCodeAsBitmapByteArr = qrCode.GetGraphic(20);

        using var ms = new MemoryStream(qrCodeAsBitmapByteArr);
        var bmp = new Bitmap(ms);
        return bmp;
    }
}
