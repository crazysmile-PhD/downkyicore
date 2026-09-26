using System.Net;
using DownKyi.Core.Storage;

namespace DownKyi.Services.Account;

internal static class BrowserCookieParser
{
    private const string BilibiliDomain = ".bilibili.com";

    public static IReadOnlyList<DownKyiCookie> ParseHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        if (header.Any(IsHeaderControlCharacter))
        {
            return [];
        }

        var value = header.Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Cookie:".Length..];
        }

        var cookies = new Dictionary<string, DownKyiCookie>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = segment[..separator].Trim();
            var cookieValue = segment[(separator + 1)..].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cookieValue)
                || !IsValidCookie(name, cookieValue))
            {
                continue;
            }

            cookies[name] = new DownKyiCookie(
                name,
                cookieValue,
                BilibiliDomain,
                isWireValue: true);
        }

        return cookies.Values.ToArray();
    }

    private static bool IsValidCookie(string name, string value)
    {
        try
        {
            _ = new Cookie(name, value, "/", BilibiliDomain);
            return true;
        }
        catch (CookieException)
        {
            return false;
        }
    }

    private static bool IsHeaderControlCharacter(char value)
    {
        return value <= '\u001f' || value == '\u007f';
    }
}
