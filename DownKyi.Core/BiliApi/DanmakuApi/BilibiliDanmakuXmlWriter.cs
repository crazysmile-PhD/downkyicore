using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DownKyi.Core.BiliApi.DanmakuApi.Models;

namespace DownKyi.Core.BiliApi.DanmakuApi;

public sealed record BilibiliDanmakuXmlWriteResult(
    XDocument Document,
    IReadOnlyList<BilibiliDanmakuXmlInvalidCharacter> InvalidCharacters);

public sealed record BilibiliDanmakuXmlInvalidCharacter(
    long Id,
    string Field,
    int Utf16Index,
    int InvalidValue,
    UnicodeCategory Category,
    string? ContextBefore,
    string? ContextAfter);

public static class BilibiliDanmakuXmlWriter
{
    private const int DiagnosticContextLength = 24;

    public static BilibiliDanmakuXmlWriteResult CreateDocument(
        IReadOnlyList<BiliDanmaku> danmakus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(danmakus);
        cancellationToken.ThrowIfCancellationRequested();

        var root = new XElement("i");
        var invalidCharacters = new List<BilibiliDanmakuXmlInvalidCharacter>();
        foreach (var danmaku in danmakus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRepresentInXml10(danmaku))
            {
                AddInvalidCharacters(invalidCharacters, danmaku, "content", danmaku.Content, includeContext: true);
                AddInvalidCharacters(invalidCharacters, danmaku, "senderHash", danmaku.MidHash, includeContext: false);
                continue;
            }

            root.Add(new XElement(
                "d",
                new XAttribute("p", CreateParameter(danmaku)),
                new XText(danmaku.Content)));
        }

        return new BilibiliDanmakuXmlWriteResult(
            new XDocument(new XDeclaration("1.0", "utf-8", null), root),
            invalidCharacters);
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

    private static void AddInvalidCharacters(
        List<BilibiliDanmakuXmlInvalidCharacter> target,
        BiliDanmaku danmaku,
        string field,
        string value,
        bool includeContext)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (XmlConvert.IsXmlChar(character))
            {
                continue;
            }

            if (char.IsHighSurrogate(character) &&
                index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            var before = includeContext ? GetContextBefore(value, index) : null;
            var after = includeContext ? GetContextAfter(value, index) : null;
            target.Add(new BilibiliDanmakuXmlInvalidCharacter(
                danmaku.Id,
                field,
                index,
                character,
                char.GetUnicodeCategory(character),
                before,
                after));
        }
    }

    private static string GetContextBefore(string value, int index)
    {
        var start = Math.Max(0, index - DiagnosticContextLength);
        if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1]))
        {
            start--;
        }

        return value[start..index];
    }

    private static string GetContextAfter(string value, int index)
    {
        var end = Math.Min(value.Length, index + 1 + DiagnosticContextLength);
        if (end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end]))
        {
            end++;
        }

        return value[(index + 1)..end];
    }
}
