using System.Text.RegularExpressions;

namespace DownKyi.CentralTestRunner;

internal sealed class SensitiveEvidenceRedactor
{
    private static readonly Regex SensitiveHeaderPattern = new(
        @"\b(?<name>authorization|proxy-authorization|cookie|set-cookie)\s*[:=]\s*[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveValuePattern = new(
        @"\b(?<name>access[_-]?token|refresh[_-]?token|token|api[_-]?key|secret|password|sessdata|bili_jct|dedeuserid|account(?:id)?|user(?:id)?|uid|mid)\b(?<separator>\s*[:=]\s*)(?<value>[^&;\s,]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UrlPattern = new(
        "\\bhttps?://[^\\s\\\"'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string workingDirectory;

    internal SensitiveEvidenceRedactor(string workingDirectory)
    {
        this.workingDirectory = workingDirectory;
    }

    internal string Redact(string value)
    {
        var redacted = ReplacePath(value, workingDirectory, "<repository-root>");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        redacted = ReplacePath(redacted, userProfile, "<user-profile>");
        redacted = SensitiveHeaderPattern.Replace(
            redacted,
            match => $"{match.Groups["name"].Value}: <redacted>");
        redacted = UrlPattern.Replace(redacted, "<redacted-url>");
        return SensitiveValuePattern.Replace(
            redacted,
            match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}<redacted>");
    }

    private static string ReplacePath(string value, string path, string replacement)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return value;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var redacted = value.Replace(trimmed, replacement, StringComparison.OrdinalIgnoreCase);
        var alternate = trimmed.Contains('\\', StringComparison.Ordinal)
            ? trimmed.Replace('\\', '/')
            : trimmed.Replace('/', '\\');
        return redacted.Replace(alternate, replacement, StringComparison.OrdinalIgnoreCase);
    }
}
