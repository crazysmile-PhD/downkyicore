using System.Diagnostics.CodeAnalysis;

namespace DownKyi.Core.Settings;

public static class AriaHttpsProxyPolicy
{
    private const string Ipv4Loopback = "127.0.0.1";
    private const string Ipv6Loopback = "::1";
    private const string Localhost = "localhost";

    public static bool TryNormalizeLocalHost(
        string? value,
        [NotNullWhen(true)] out string? normalizedHost)
    {
        normalizedHost = value?.Trim();
        if (string.IsNullOrEmpty(normalizedHost))
        {
            normalizedHost = null;
            return false;
        }

        if (normalizedHost.Length > 2
            && normalizedHost[0] == '['
            && normalizedHost[^1] == ']')
        {
            normalizedHost = normalizedHost[1..^1];
        }

        if (string.Equals(normalizedHost, Localhost, StringComparison.OrdinalIgnoreCase))
        {
            normalizedHost = Localhost;
            return true;
        }

        if (string.Equals(normalizedHost, Ipv4Loopback, StringComparison.Ordinal)
            || string.Equals(normalizedHost, Ipv6Loopback, StringComparison.Ordinal))
        {
            return true;
        }

        normalizedHost = null;
        return false;
    }

    public static bool TryCreateConnectProxyUri(
        string? host,
        int port,
        [NotNullWhen(true)] out Uri? proxyUri)
    {
        proxyUri = null;
        if (port is < 1 or > 65535
            || !TryNormalizeLocalHost(host, out var normalizedHost))
        {
            return false;
        }

        proxyUri = new UriBuilder(Uri.UriSchemeHttp, normalizedHost, port).Uri;
        return true;
    }
}
