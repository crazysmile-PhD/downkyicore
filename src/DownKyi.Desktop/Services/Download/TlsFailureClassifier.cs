using System;
using System.Net.Http;
using System.Security.Authentication;

namespace DownKyi.Services.Download;

internal static class TlsFailureClassifier
{
    private const string Prefix = "download.transfer.tls.";

    public static bool TryClassify(Exception? exception, out string errorCode)
    {
        var current = exception;
        var isSecureConnectionFailure = false;
        while (current != null)
        {
            if (current is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.SecureConnectionError
                })
            {
                isSecureConnectionFailure = true;
            }

            if (current is AuthenticationException)
            {
                isSecureConnectionFailure = true;
            }

            if (TryClassify(current.Message, out errorCode))
            {
                return true;
            }

            current = current.InnerException;
        }

        errorCode = isSecureConnectionFailure ? Prefix + "handshake" : string.Empty;
        return isSecureConnectionFailure;
    }

    public static bool TryClassify(string? message, out string errorCode)
    {
        if (ContainsAny(message,
                "not yet valid",
                "not valid yet",
                "尚未生效",
                "尚未有效"))
        {
            errorCode = Prefix + "not-yet-valid";
            return true;
        }

        if (ContainsAny(message,
                "expired",
                "validity period",
                "已过期",
                "已過期",
                "800b0101"))
        {
            errorCode = Prefix + "expired";
            return true;
        }

        if (ContainsAny(message,
                "hostname",
                "host name",
                "common name",
                "does not match",
                "wrong principal",
                "名称不匹配",
                "名稱不匹配",
                "80090322",
                "800b010f"))
        {
            errorCode = Prefix + "hostname";
            return true;
        }

        if (ContainsAny(message,
                "untrusted",
                "unknown ca",
                "self-signed",
                "self signed",
                "unable to get local issuer",
                "unable to verify the first certificate",
                "不受信任",
                "80090325",
                "800b0109"))
        {
            errorCode = Prefix + "untrusted";
            return true;
        }

        if (ContainsAny(message,
                "certificate chain",
                "chain building",
                "issuer certificate",
                "certificate verify failed",
                "certificate verification failed",
                "wrong chain",
                "800b010a"))
        {
            errorCode = Prefix + "chain";
            return true;
        }

        if (ContainsAny(message,
                "ssl/tls handshake",
                "tls handshake",
                "ssl handshake",
                "secure connection",
                "authentication failed",
                "certificate"))
        {
            errorCode = Prefix + "handshake";
            return true;
        }

        errorCode = string.Empty;
        return false;
    }

    public static bool IsTlsErrorCode(string? errorCode)
    {
        return errorCode?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    public static string GetResourceKey(string errorCode)
    {
        return errorCode switch
        {
            Prefix + "untrusted" => "DownloadTlsUntrusted",
            Prefix + "expired" => "DownloadTlsExpired",
            Prefix + "not-yet-valid" => "DownloadTlsNotYetValid",
            Prefix + "hostname" => "DownloadTlsHostnameMismatch",
            Prefix + "chain" => "DownloadTlsInvalidChain",
            _ => "DownloadTlsHandshakeFailed"
        };
    }

    private static bool ContainsAny(string? value, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
