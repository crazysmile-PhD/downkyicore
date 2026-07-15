using System.Text.RegularExpressions;

namespace DownKyi.Core.Logging;

internal interface ISensitiveDataRedactor
{
    string Redact(string? text);
}

internal sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = QuerySecretRegex().Replace(text, "$1[redacted]");
        redacted = CookieRegex().Replace(redacted, "$1=[redacted]");
        redacted = EmailRegex().Replace(redacted, "[email]");
        redacted = QuotedWindowsPathRegex().Replace(redacted, RedactPath);
        redacted = WindowsPathRegex().Replace(redacted, RedactPath);
        redacted = UnixUserPathRegex().Replace(redacted, RedactPath);
        return UserIdRegex().Replace(redacted, "$1=[redacted]");
    }

    private static string RedactPath(Match match)
    {
        var value = match.Value.TrimEnd();
        var suffix = value.Length == match.Value.Length ? string.Empty : match.Value[value.Length..];
        var fileName = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"[path]{suffix}"
            : $"[path]{Path.DirectorySeparatorChar}{fileName}{suffix}";
    }

    [GeneratedRegex("(?i)([?&](?:SESSDATA|bili_jct|DedeUserID|DedeUserID__ckMd5|sid|access_key|csrf|token|secret|password)=)[^&#\\s\"']+")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex("(?i)(cookie|set-cookie|SESSDATA|bili_jct|DedeUserID|DedeUserID__ckMd5|sid|access_key|csrf|token|secret|password)\\s*[:=]\\s*[^\\s;,&\"']+")]
    private static partial Regex CookieRegex();

    [GeneratedRegex("(?i)[a-z0-9._%+\\-]+@[a-z0-9.\\-]+\\.[a-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex("(?i)(?<=['\"])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\r\\n\"']+(?=['\"])")]
    private static partial Regex QuotedWindowsPathRegex();

    [GeneratedRegex("(?i)(?<![\\w])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\r\\n\\s\"'<>|]+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?<!:)\\b(?:/Users/|/home/|/var/folders/|/tmp/)[^\\r\\n\\s\"'<>|]+")]
    private static partial Regex UnixUserPathRegex();

    [GeneratedRegex("(?i)(mid|uid|userid|user_id)\\s*[:=]\\s*\\d{4,}")]
    private static partial Regex UserIdRegex();
}
