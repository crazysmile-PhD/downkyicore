using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DownKyi.Core.BiliApi.DanmakuApi.Models;

namespace DownKyi.Core.BiliApi.DanmakuApi;

public static class BilibiliDanmakuXmlWriter
{
    public static XDocument CreateDocument(
        IReadOnlyList<BiliDanmaku> danmakus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(danmakus);
        cancellationToken.ThrowIfCancellationRequested();

        var root = new XElement("i");
        foreach (var danmaku in danmakus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRepresentInXml10(danmaku))
            {
                continue;
            }

            root.Add(new XElement(
                "d",
                new XAttribute("p", CreateParameter(danmaku)),
                new XText(danmaku.Content)));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static string CreateParameter(BiliDanmaku danmaku)
    {
        return string.Join(
            ',',
            (danmaku.Progress / 1000m).ToString("0.000", CultureInfo.InvariantCulture),
            danmaku.Mode.ToString(CultureInfo.InvariantCulture),
            danmaku.Fontsize.ToString(CultureInfo.InvariantCulture),
            danmaku.Color.ToString(CultureInfo.InvariantCulture),
            danmaku.Ctime.ToString(CultureInfo.InvariantCulture),
            danmaku.Pool.ToString(CultureInfo.InvariantCulture),
            danmaku.MidHash,
            danmaku.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static bool CanRepresentInXml10(BiliDanmaku danmaku)
    {
        try
        {
            XmlConvert.VerifyXmlChars(danmaku.Content);
            XmlConvert.VerifyXmlChars(danmaku.MidHash);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
