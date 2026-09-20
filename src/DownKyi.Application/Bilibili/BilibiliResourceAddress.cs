namespace DownKyi.Application.Bilibili;

public static class BilibiliResourceAddress
{
    public static bool TryNormalizeHttps(string? address, out string normalizedAddress)
    {
        normalizedAddress = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var candidate = address.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = $"{Uri.UriSchemeHttps}:{candidate}";
        }
        else if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{Uri.UriSchemeHttps}://{candidate["http://".Length..]}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        normalizedAddress = candidate;
        return true;
    }
}
