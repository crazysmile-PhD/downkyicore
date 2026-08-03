using System;

namespace DownKyi.Services.Download;

internal static class Aria2TransferFailureClassifier
{
    public static DownloadTransferResult Classify(
        string? errorCode,
        string? errorMessage)
    {
        if (string.Equals(errorCode, "33", StringComparison.Ordinal))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.insecure-redirect");
        }

        if (string.Equals(errorCode, "34", StringComparison.Ordinal))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.credentialed-redirect");
        }

        if (TlsFailureClassifier.TryClassify(errorMessage, out var tlsErrorCode))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Tls,
                tlsErrorCode);
        }

        if (errorMessage?.Contains(
                "HTTPS redirect downgrade rejected by DownKyi policy",
                StringComparison.Ordinal) == true)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.insecure-redirect");
        }

        if (errorMessage?.Contains(
                "Cross-origin redirect with sensitive headers rejected by DownKyi policy",
                StringComparison.Ordinal) == true)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.credentialed-redirect");
        }

        if (ContainsHttpStatus(errorMessage, "403") ||
            ContainsHttpStatus(errorMessage, "404"))
        {
            return Failed(
                DownloadTransferFailureKind.ExpiredAddress,
                errorCode);
        }

        if (ContainsHttpStatus(errorMessage, "429"))
        {
            return Failed(
                DownloadTransferFailureKind.RateLimited,
                errorCode);
        }

        if (ContainsHttpStatus(errorMessage, "500") ||
            ContainsHttpStatus(errorMessage, "502") ||
            ContainsHttpStatus(errorMessage, "503") ||
            ContainsHttpStatus(errorMessage, "504"))
        {
            return Failed(
                DownloadTransferFailureKind.TransientNetwork,
                errorCode);
        }

        return errorCode switch
        {
            "2" or "5" or "6" or "7" or "19" or "29" =>
                Failed(DownloadTransferFailureKind.TransientNetwork, errorCode),
            "3" or "4" or "23" =>
                Failed(DownloadTransferFailureKind.ExpiredAddress, errorCode),
            "8" or "10" =>
                Failed(DownloadTransferFailureKind.ResumeRejected, errorCode),
            "32" =>
                Failed(DownloadTransferFailureKind.InvalidMedia, errorCode),
            "9" or "13" or "14" or "15" or "16" or "17" or "18" =>
                Failed(DownloadTransferFailureKind.Disk, errorCode),
            "not-found" =>
                Failed(DownloadTransferFailureKind.ExpiredAddress, errorCode),
            _ => Failed(DownloadTransferFailureKind.Permanent, errorCode)
        };
    }

    private static DownloadTransferResult Failed(
        DownloadTransferFailureKind kind,
        string? ariaErrorCode)
    {
        var sanitizedCode = string.IsNullOrWhiteSpace(ariaErrorCode)
            ? "download.transfer.aria2"
            : $"download.transfer.aria2-{ariaErrorCode}";
        return DownloadTransferResult.Failed(kind, sanitizedCode);
    }

    private static bool ContainsHttpStatus(string? message, string statusCode)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var searchIndex = 0;
        while ((searchIndex = message.IndexOf(
                   statusCode,
                   searchIndex,
                   StringComparison.Ordinal)) >= 0)
        {
            var beforeIsDigit = searchIndex > 0 && char.IsDigit(message[searchIndex - 1]);
            var afterIndex = searchIndex + statusCode.Length;
            var afterIsDigit = afterIndex < message.Length && char.IsDigit(message[afterIndex]);
            if (!beforeIsDigit && !afterIsDigit)
            {
                return true;
            }

            searchIndex = afterIndex;
        }

        return false;
    }
}
