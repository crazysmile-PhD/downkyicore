using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;

namespace DownKyi.Services.Download;

internal sealed partial class Aria2TransferBackend
{
    public async Task<DownloadTransferResult> ResetAsync(
        string? backendIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(backendIdentity))
        {
            return DownloadTransferResult.Succeeded();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var removed = await _ariaClient
                .ForceRemoveAsync(backendIdentity, timeout.Token)
                .ConfigureAwait(true);
            if (removed.Error != null && !IsNotFound(removed.Error))
            {
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.TransientNetwork,
                    "download.transfer.aria2-reset-rejected");
            }

            var resultRemoved = await _ariaClient
                .RemoveDownloadResultAsync(backendIdentity, timeout.Token)
                .ConfigureAwait(true);
            if (resultRemoved.Error != null && !IsNotFound(resultRemoved.Error))
            {
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.TransientNetwork,
                    "download.transfer.aria2-reset-result-rejected");
            }

            return DownloadTransferResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or HttpRequestException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarningMessage(
                $"aria2 transfer reset failed; type={exception.GetType().Name}.");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-reset-rpc");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 transfer reset contract failed; type={exception.GetType().Name}.");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-reset-contract");
        }
    }
}
