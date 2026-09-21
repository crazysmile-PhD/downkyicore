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
        while (current != null)
        {
            if (TryClassify(current.Message, out errorCode))
            {
                return true;
            }

            current = current.InnerException;
        }

        errorCode = string.Empty;
        return false;
    }

    public static bool IsSecureConnectionFailure(Exception? exception)
    {
        var current = exception;
        while (current != null)
        {
            if (current is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.SecureConnectionError
                } or AuthenticationException ||
                IsSecureConnectionFailure(current.Message))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    public static bool TryClassify(string? message, out string errorCode)
    {
        if (ContainsAny(message,
                "certificate is not yet valid",
                "certificate not yet valid",
                "certificate is not valid yet",
                "证书尚未生效",
                "憑證尚未生效"))
        {
            errorCode = Prefix + "not-yet-valid";
            return true;
        }

        if (ContainsAny(message,
                "certificate has expired",
                "certificate is expired",
                "certificate expired",
                "certificate validity period",
                "证书已过期",
                "憑證已過期",
                "800b0101"))
        {
            errorCode = Prefix + "expired";
            return true;
        }

        if (ContainsAny(message,
                "certificate hostname",
                "certificate host name",
                "certificate common name",
                "remote certificate name mismatch",
                "certificate name does not match",
                "no alternative certificate subject name matches",
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
                "untrustedroot",
                "unable to get local issuer",
                "unable to verify the first certificate",
                "不受信任",
                "80090325",
                "800b0109") ||
            ContainsAny(message, "not trusted") &&
            ContainsAny(message,
                "certificate",
                "certificate authority",
                "issuer",
                "chain",
                "root"))
        {
            errorCode = Prefix + "untrusted";
            return true;
        }

        if (ContainsAny(message,
                "certificate chain",
                "chain building",
                "issuer certificate",
                "certificate verify failed",
                "certificate verification",
                "wrong chain",
                "remotecertificatechainerrors",
                "800b010a"))
        {
            errorCode = Prefix + "chain";
            return true;
        }

        if (ContainsAny(message,
                "remote certificate is invalid",
                "certificate is invalid",
                "certificate validation failed",
                "certificate validation failure",
                "certificate validation error",
                "certificate was rejected",
                "certificate rejected"))
        {
            errorCode = Prefix + "handshake";
            return true;
        }

        errorCode = string.Empty;
        return false;
    }

    public static bool IsSecureConnectionFailure(string? message)
    {
        return IsTlsHandshakeFailure(message) || ContainsAny(message,
            "secure connection",
            "ssl connection",
            "tls connection",
            "transport stream",
            "transport connection");
    }

    public static bool IsTlsHandshakeFailure(string? message)
    {
        return ContainsAny(message,
            "ssl/tls handshake",
            "tls handshake",
            "ssl handshake");
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
