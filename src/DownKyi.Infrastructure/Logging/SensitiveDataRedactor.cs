using System.Text.RegularExpressions;

namespace DownKyi.Infrastructure.Logging;

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

        var redacted = UrlRegex().Replace(text, RedactUrl);
        redacted = CredentialHeaderRegex().Replace(redacted, "$1=[redacted]");
        redacted = SensitiveValueRegex().Replace(redacted, RedactValue);
        redacted = EmailRegex().Replace(redacted, "[email]");
        redacted = QuotedPathRegex().Replace(redacted, "[path]");
        redacted = WindowsPathRegex().Replace(redacted, "[path]");
        return UnixPathRegex().Replace(redacted, "[path]");
    }

    private static string RedactUrl(Match match)
    {
        var value = match.Value.StartsWith("//", StringComparison.Ordinal) ? $"https:{match.Value}" : match.Value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[url]";
        }

        return new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }

    private static string RedactValue(Match match)
    {
        var value = match.Groups[2].Value;
        var quote = value[0] is '\"' or '\'' ? value[..1] : string.Empty;
        return $"{match.Groups[1].Value}{quote}[redacted]{quote}";
    }

    // Keep the server and endpoint; remove credentials and all query/fragment values.
    [GeneratedRegex("(?i)(?:\\b[a-z][a-z0-9+.-]*:)?//[^\\s\"'<>]+")]
    private static partial Regex UrlRegex();

    // A header can contain several credentials; suppress the whole value.
    [GeneratedRegex("(?i)\\b(authorization|proxy-authorization|cookie|set-cookie)[\"']?\\s*[:=]\\s*[^\\r\\n]+")]
    private static partial Regex CredentialHeaderRegex();

    [GeneratedRegex("(?i)\\b((?:SESSDATA|bili_jct|DedeUserID|DedeUserID__ckMd5|sid|access_key|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|csrf|token|secret|password|mid|uid|userid|user_id|username|user_name)[\"']?\\s*[:=]\\s*)(\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s;,&{}\"']+)")]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex("(?i)[a-z0-9._%+\\-]+@[a-z0-9.\\-]+\\.[a-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex("(?i)(?<=['\"])(?:[a-z]:[\\\\/]|\\\\\\\\|/(?!/))[^\\r\\n\"']+(?=['\"])")]
    private static partial Regex QuotedPathRegex();

    [GeneratedRegex("(?i)(?<![\\w])(?:[a-z]:[\\\\/]|\\\\\\\\)[^;\\r\\n\"'<>|]+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?<![^\\s=:\"'(])/(?!/)[^;\\r\\n\"'<>|]+")]
    private static partial Regex UnixPathRegex();
}
