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
    private readonly DownloadTransferWatchdog _watchdog;
    private readonly Func<bool> _isBandwidthLimited;

    public DownloadTransferCoordinator(
        ITransferBackend backend,
        DownloadRetryPolicy retryPolicy,
        TimeProvider timeProvider,
        ILogger<DownloadTransferCoordinator> logger,
        Func<bool>? isBandwidthLimited = null,
        DownloadTransferWatchdog? watchdog = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isBandwidthLimited = isBandwidthLimited ?? (static () => false);
        _watchdog = watchdog ?? DownloadTransferWatchdog.Default;
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

        // Recovery keeps the original resource affinity. A fresh transfer surveys
        // a bounded set of replicas; observations never become identity evidence.
        var target = Path.Combine(request.Directory, request.FileName);
        if (_backend is IDownloadSourceProbe probe && addresses.Length > 1 &&
            !_isBandwidthLimited() && !File.Exists(target) && !File.Exists($"{target}.download"))
        {
            var surveyed = await Task.WhenAll(addresses.Take(3).Select(async address =>
                (Address: address, Observation: await probe.ProbeAsync(address, cancellationToken)
                    .ConfigureAwait(false)))).ConfigureAwait(true);
            var preferred = surveyed
                .OrderByDescending(static source => source.Observation is DownloadSourceReady ready
                    ? ready.Bytes / Math.Max(ready.Elapsed.TotalSeconds, 0.001) : 0)
                .Select(static source => source.Address);
            addresses = preferred.Concat(addresses.Skip(surveyed.Length)).ToArray();
            _logger.LogInformationMessage($"Download source survey completed; candidates={surveyed.Length}; " +
                $"ready={surveyed.Count(static source => source.Observation is DownloadSourceReady)}.");
        }

        var addressIndex = 0;
        var rangeSchedulingPolicy = new BuiltinRangeSchedulingPolicy(_timeProvider);
        var attemptsForAddress = 0;
        var resumeCleanupRetryGrantedForAddress = false;
        var canRefreshAddresses = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptsForAddress++;
            var currentAddress = addresses[addressIndex];
            var attemptRequest = request with
            {
                Urls = [currentAddress],
                CancellationToken = cancellationToken,
                RangeSchedulingPolicy = rangeSchedulingPolicy
            };
            var lease = new DownloadTransferAttempt(_timeProvider, _watchdog, _isBandwidthLimited, _logger);
            var lastResult = await lease.RunAsync(_backend, attemptRequest, cancellationToken)
                .ConfigureAwait(true);
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

            DownloadRetryDecision decision;
            if (lastResult.FailureKind == DownloadTransferFailureKind.ResumeRejected
                && !resumeCleanupRetryGrantedForAddress
                && attemptsForAddress < _retryPolicy.MaximumAttempts)
            {
                resumeCleanupRetryGrantedForAddress = true;
                decision = new DownloadRetryDecision(
                    DownloadRetryAction.RetrySameAddress,
                    TimeSpan.Zero);
            }
            else
            {
                decision = _retryPolicy.Decide(
                    lastResult,
                    attemptsForAddress,
                    addressIndex + 1 < addresses.Length,
                    canRefreshAddresses);
            }
            _logger.LogWarningMessage(FormatFailureDiagnostic(
                _backend.Name,
                attemptsForAddress,
                _retryPolicy.MaximumAttempts,
                lastResult.FailureKind,
                lastResult.ErrorCode,
                decision.Action,
                decision.Delay));
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
                    var sourceChangeFailure = await ClearArtifactsForSourceChangeAsync(
                        request,
                        currentAddress,
                        addresses[nextAddressIndex],
                        deleteArtifacts: lastResult.FailureKind !=
                            DownloadTransferFailureKind.InvalidMedia,
                        cancellationToken: cancellationToken).ConfigureAwait(true);
                    if (sourceChangeFailure != null)
                    {
                        return sourceChangeFailure;
                    }

                    addressIndex = nextAddressIndex;
                    attemptsForAddress = 0;
                    resumeCleanupRetryGrantedForAddress = false;
                    break;
                case DownloadRetryAction.RefreshAddresses:
                    var refreshedAddresses = NormalizeAddresses(
                        await refreshAddressesAsync(cancellationToken).ConfigureAwait(true));
                    if (refreshedAddresses.Length == 0)
                    {
                        return lastResult;
                    }

                    var refreshChangeFailure = await ClearArtifactsForSourceChangeAsync(
                        request,
                        currentAddress,
                        refreshedAddresses[0],
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
                    resumeCleanupRetryGrantedForAddress = false;
                    canRefreshAddresses = false;
                    break;
                case DownloadRetryAction.Stop:
                default:
                    return lastResult;
            }
        }
    }

    internal static string FormatFailureDiagnostic(
        string backend,
        int attempt,
        int maximumAttempts,
        DownloadTransferFailureKind failureKind,
        string errorCode,
        DownloadRetryAction nextAction,
        TimeSpan delay)
    {
        var delayMilliseconds = delay.Ticks / TimeSpan.TicksPerMillisecond;
        return FormattableString.Invariant(
            $"Download transfer attempt failed; backend={backend}; attempt={attempt}/{maximumAttempts}; failure={failureKind}; error={errorCode}; next={nextAction}; delayMilliseconds={delayMilliseconds}");
    }

    private async Task<DownloadTransferResult?> ClearArtifactsForSourceChangeAsync(
        DownloadTransferRequest request,
        string currentAddress,
        string nextAddress,
        bool deleteArtifacts,
        CancellationToken cancellationToken)
    {
        if (string.Equals(currentAddress, nextAddress, StringComparison.Ordinal))
        {
            return null;
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
                    "download.transfer.source-change-cleanup");
            }
        }

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
