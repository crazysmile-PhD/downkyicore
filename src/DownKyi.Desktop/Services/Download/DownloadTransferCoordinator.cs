using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTransferCoordinator
{
    private readonly ITransferBackend _backend;
    private readonly DownloadRetryPolicy _retryPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DownloadTransferCoordinator> _logger;

    public DownloadTransferCoordinator(
        ITransferBackend backend,
        DownloadRetryPolicy retryPolicy,
        TimeProvider timeProvider,
        ILogger<DownloadTransferCoordinator> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DownloadTransferResult> TransferAsync(
        DownloadTransferRequest request,
        Func<CancellationToken, Task<IReadOnlyList<string>>> refreshAddressesAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(refreshAddressesAsync);
        var addresses = NormalizeAddresses(request.Urls);
        if (addresses.Length == 0)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.no-address");
        }

        var addressIndex = 0;
        var attemptsForAddress = 0;
        var canRefreshAddresses = true;
        var backendIdentity = request.BackendIdentity;
        async Task SetBackendIdentityAsync(
            string? value,
            CancellationToken token)
        {
            await request.SetBackendIdentityAsync(value, token).ConfigureAwait(true);
            backendIdentity = value;
        }

        var lastResult = DownloadTransferResult.Failed(
            DownloadTransferFailureKind.Permanent,
            "download.transfer.not-started");
        for (var attempt = 1; attempt <= _retryPolicy.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptsForAddress++;
            var currentAddress = addresses[addressIndex];
            var attemptRequest = request with
            {
                BackendIdentity = backendIdentity,
                Urls = [currentAddress],
                SetBackendIdentityAsync = SetBackendIdentityAsync,
                CancellationToken = cancellationToken
            };
            lastResult = await _backend.TransferAsync(attemptRequest).ConfigureAwait(true);
            if (lastResult.Outcome != DownloadTransferOutcome.Failed)
            {
                return lastResult;
            }

            if (lastResult.FailureKind is DownloadTransferFailureKind.InvalidMedia
                or DownloadTransferFailureKind.ResumeRejected)
            {
                var cleanup = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                    Path.Combine(request.Directory, request.FileName),
                    _logger);
                if (!cleanup.Succeeded)
                {
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.Disk,
                        "download.transfer.cleanup-failed");
                }
            }

            var decision = _retryPolicy.Decide(
                lastResult,
                attempt,
                attemptsForAddress,
                addressIndex + 1 < addresses.Length,
                canRefreshAddresses);
            _logger.LogWarningMessage(
                $"Download transfer attempt failed; " +
                $"backend={_backend.Name}; " +
                $"attempt={attempt}/{_retryPolicy.MaximumAttempts}; " +
                $"failure={lastResult.FailureKind}; " +
                $"error={lastResult.ErrorCode}; " +
                $"next={decision.Action}; " +
                $"delaySeconds={decision.Delay.TotalSeconds:0.###}");
            if (decision.Delay > TimeSpan.Zero)
            {
                await Task.Delay(
                    decision.Delay,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(true);
            }

            switch (decision.Action)
            {
                case DownloadRetryAction.RetrySameAddress:
                    break;
                case DownloadRetryAction.TryNextAddress:
                    var nextAddressIndex = addressIndex + 1;
                    var sourceChangeFailure = await ResetForSourceChangeAsync(
                        request,
                        currentAddress,
                        addresses[nextAddressIndex],
                        backendIdentity,
                        SetBackendIdentityAsync,
                        cancellationToken).ConfigureAwait(true);
                    if (sourceChangeFailure != null)
                    {
                        return sourceChangeFailure;
                    }

                    addressIndex = nextAddressIndex;
                    attemptsForAddress = 0;
                    break;
                case DownloadRetryAction.RefreshAddresses:
                    var refreshedAddresses = NormalizeAddresses(
                        await refreshAddressesAsync(cancellationToken).ConfigureAwait(true));
                    if (refreshedAddresses.Length == 0)
                    {
                        return lastResult;
                    }

                    var refreshChangeFailure = await ResetForSourceChangeAsync(
                        request,
                        currentAddress,
                        refreshedAddresses[0],
                        backendIdentity,
                        SetBackendIdentityAsync,
                        cancellationToken).ConfigureAwait(true);
                    if (refreshChangeFailure != null)
                    {
                        return refreshChangeFailure;
                    }

                    addresses = refreshedAddresses;
                    addressIndex = 0;
                    attemptsForAddress = 0;
                    canRefreshAddresses = false;
                    break;
                case DownloadRetryAction.Stop:
                default:
                    return lastResult;
            }
        }

        return lastResult;
    }

    private async Task<DownloadTransferResult?> ResetForSourceChangeAsync(
        DownloadTransferRequest request,
        string currentAddress,
        string nextAddress,
        string? backendIdentity,
        Func<string?, CancellationToken, Task> setBackendIdentityAsync,
        CancellationToken cancellationToken)
    {
        if (string.Equals(currentAddress, nextAddress, StringComparison.Ordinal))
        {
            return null;
        }

        var resetResult = await _backend
            .ResetAsync(backendIdentity, cancellationToken)
            .ConfigureAwait(true);
        if (resetResult.Outcome != DownloadTransferOutcome.Succeeded)
        {
            return resetResult;
        }

        var cleanup = await DownloadTransferFileCleanup.DeleteInvalidArtifactsAsync(
                Path.Combine(request.Directory, request.FileName),
                _logger,
                _timeProvider,
                cancellationToken).ConfigureAwait(true);
        if (!cleanup.Succeeded)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Disk,
                "download.transfer.source-change-cleanup");
        }

        await setBackendIdentityAsync(null, cancellationToken).ConfigureAwait(true);
        _logger.LogInformationMessage(
            $"Download transfer source changed; backend={_backend.Name}; partialState=cleared.");
        return null;
    }

    private static string[] NormalizeAddresses(IEnumerable<string> addresses)
    {
        return addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
