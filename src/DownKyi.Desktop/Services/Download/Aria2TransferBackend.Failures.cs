using System;
using System.Net.Http;

namespace DownKyi.Services.Download;

internal sealed partial class Aria2TransferBackend
{
    internal static DownloadTransferResult ClassifyHttpTransportFailure(
        HttpRequestException exception,
        string transientErrorCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(transientErrorCode);
        if (!TlsFailureClassifier.TryClassify(exception, out var tlsErrorCode))
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                transientErrorCode);
        }

        return TlsFailureClassifier.CreateTransferFailure(
            tlsErrorCode,
            transientErrorCode);
    }

    internal static string GetSafeHost(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri.IdnHost
            : "invalid";
    }
}
