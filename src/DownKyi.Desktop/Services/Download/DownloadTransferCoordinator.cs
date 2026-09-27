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
                    request.StagingDirectory,
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
                        deleteArtifacts: lastResult.FailureKind !=
                            DownloadTransferFailureKind.InvalidMedia,
                        cancellationToken: cancellationToken).ConfigureAwait(true);
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
                        deleteArtifacts: lastResult.FailureKind !=
                            DownloadTransferFailureKind.InvalidMedia,
                        cancellationToken: cancellationToken).ConfigureAwait(true);
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
                    if (lastResult.FailureKind == DownloadTransferFailureKind.InvalidMedia
                        && backendIdentity != null)
                    {
                        var terminalResetFailure = await ResetTransferStateAsync(
                            request,
                            backendIdentity,
                            SetBackendIdentityAsync,
                            "download.transfer.cleanup-failed",
                            deleteArtifacts: false,
                            cancellationToken: cancellationToken).ConfigureAwait(true);
                        if (terminalResetFailure != null)
                        {
                            return terminalResetFailure;
                        }
                    }

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
        bool deleteArtifacts,
        CancellationToken cancellationToken)
    {
        if (string.Equals(currentAddress, nextAddress, StringComparison.Ordinal))
        {
            return null;
        }

        var resetFailure = await ResetTransferStateAsync(
            request,
            backendIdentity,
            setBackendIdentityAsync,
            "download.transfer.source-change-cleanup",
            deleteArtifacts,
            cancellationToken).ConfigureAwait(true);
        if (resetFailure != null)
        {
            return resetFailure;
        }

        _logger.LogInformationMessage(
            $"Download transfer source changed; backend={_backend.Name}; partialState=cleared.");
        return null;
    }

    private async Task<DownloadTransferResult?> ResetTransferStateAsync(
        DownloadTransferRequest request,
        string? backendIdentity,
        Func<string?, CancellationToken, Task> setBackendIdentityAsync,
        string cleanupFailureCode,
        bool deleteArtifacts,
        CancellationToken cancellationToken)
    {
        var resetResult = await _backend
            .ResetAsync(backendIdentity, cancellationToken)
            .ConfigureAwait(true);
        if (resetResult.Outcome != DownloadTransferOutcome.Succeeded)
        {
            return resetResult;
        }

        if (deleteArtifacts)
        {
            var cleanup = await DownloadTransferFileCleanup.DeleteInvalidArtifactsAsync(
                    Path.Combine(request.Directory, request.FileName),
                    request.StagingDirectory,
                    _logger,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(true);
            if (!cleanup.Succeeded)
            {
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.Disk,
                    cleanupFailureCode);
            }
        }

        await setBackendIdentityAsync(null, cancellationToken).ConfigureAwait(true);
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
