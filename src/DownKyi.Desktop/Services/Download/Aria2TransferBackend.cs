using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed partial class Aria2TransferBackend : ITransferBackend
{
    private static readonly TimeSpan PauseStatusPollInterval = TimeSpan.FromMilliseconds(25);
    private readonly AriaClient _ariaClient;
    private readonly AriaRuntimeClientRegistry _clientRegistry;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly AriaDownloadAddressResolver _addressResolver;
    private readonly Aria2RuntimeLifecycle _runtimeLifecycle;
    private readonly Uri? _httpsProxyAddress;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NetworkApplicationSettings _networkSettings;
    private readonly ILogger<Aria2TransferBackend> _logger;
    private IDisposable? _runtimeRegistration;

    public Aria2TransferBackend(
        NetworkApplicationSettings networkSettings,
        AriaClient ariaClient,
        AriaRuntimeClientRegistry clientRegistry,
        DownloadDiagnosticLogger diagnosticLogger,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory,
        ILogger<Aria2TransferBackend> logger,
        bool ownsAriaServer,
        LocalAriaRpcEndpoint? localEndpoint)
    {
        _networkSettings = networkSettings ?? throw new ArgumentNullException(nameof(networkSettings));
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _clientRegistry = clientRegistry ?? throw new ArgumentNullException(nameof(clientRegistry));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _httpsProxyAddress = ResolveHttpsProxyAddress(networkSettings);
        _addressResolver = AriaDownloadAddressResolver.Create(_httpsProxyAddress);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeLifecycle = new Aria2RuntimeLifecycle(
            networkSettings,
            ariaClient,
            diagnosticLogger,
            ariaServer,
            loggerFactory.CreateLogger<Aria2RuntimeLifecycle>(),
            ownsAriaServer,
            localEndpoint);
    }

    public string Name => _runtimeLifecycle.Name;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtimeRegistration ??= _clientRegistry.Activate(_ariaClient);
        try
        {
            await _runtimeLifecycle.StartAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            _runtimeLifecycle.AbortStartup();
            ReleaseRuntimeRegistration();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _runtimeLifecycle.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            ReleaseRuntimeRegistration();
        }
    }

    public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Urls.Count != 1)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.single-address-required");
        }

        string? activeGid;
        try
        {
            var preparation = await EnsureAriaTaskAsync(
                request).ConfigureAwait(true);
            if (preparation.ErrorCode != null)
            {
                _logger.LogWarningMessage(
                    $"aria2 download address was rejected; code={preparation.ErrorCode}");
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.Permanent,
                    preparation.ErrorCode);
            }

            activeGid = preparation.Gid
                ?? throw new InvalidOperationException("The prepared aria2 task identifier is missing.");
        }
        catch (HttpRequestException exception) when (
            TlsFailureClassifier.TryClassify(exception, out var tlsErrorCode))
        {
            _logger.LogWarningMessage(
                $"aria2 address validation failed TLS policy; code={tlsErrorCode}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Tls,
                tlsErrorCode);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC transport failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC contract failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-contract");
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC request was rejected; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-rejected");
        }

        _diagnosticLogger.LogAriaTaskStart(Name, activeGid, request.Urls.Count, _networkSettings);
        var ariaManager = new AriaManager(
            _ariaClient,
            _loggerFactory.CreateLogger<AriaManager>());
        DownloadProgress? lastProgress = null;
        EventHandler<AriaProgressEventArgs> progressHandler = (_, eventArgs) =>
        {
            var progress = CreateProgress(activeGid, eventArgs);
            if (progress != null)
            {
                lastProgress = progress;
                request.PublishProgress(progress);
            }
        };
        ariaManager.TellStatus += progressHandler;
        try
        {
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
            var statusTask = ariaManager.GetDownloadStatusDetailAsync(
                activeGid,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!request.IsPauseRequested())
                    {
                        request.EnsureActive();
                    }

                    return ValueTask.CompletedTask;
                },
                monitorCancellation.Token);
            var pauseRequestTask = request.WaitForPauseRequestedAsync(
                monitorCancellation.Token);
            var completedTask = await Task.WhenAny(statusTask, pauseRequestTask)
                .ConfigureAwait(true);
            if (ReferenceEquals(completedTask, pauseRequestTask))
            {
                await pauseRequestTask.ConfigureAwait(true);
                request.CancellationToken.ThrowIfCancellationRequested();
                var checkpoint = await PauseAndWaitForCheckpointAsync(
                    activeGid,
                    request.CancellationToken).ConfigureAwait(true);
                if (checkpoint == AriaPauseCheckpoint.Paused)
                {
                    await monitorCancellation.CancelAsync().ConfigureAwait(true);
                    await ObserveExpectedMonitorCancellationAsync(
                        statusTask,
                        request.CancellationToken).ConfigureAwait(true);

                    return DownloadTransferResult.Paused();
                }
            }

            var (downloadResult, errorCode, errorMessage) =
                await statusTask.ConfigureAwait(true);
            await monitorCancellation.CancelAsync().ConfigureAwait(true);
            await ObserveExpectedMonitorCancellationAsync(
                pauseRequestTask,
                request.CancellationToken).ConfigureAwait(true);

            if (downloadResult == DownloadResult.SUCCESS)
            {
                var finalFile = Path.Combine(request.Directory, request.FileName);
                var integrity = DownloadFileIntegrity.Check(
                    finalFile,
                    request.ExpectedBytes);
                if (!integrity.IsUsable)
                {
                    _logger.LogInformationMessage(
                        integrity.Reason ?? "Downloaded media file is not usable.");
                    return DownloadTransferResult.Failed(
                        DownloadTransferFailureKind.InvalidMedia,
                        "download.transfer.invalid-media");
                }

                return DownloadTransferResult.Succeeded();
            }

            if (ShouldClearBackendIdentity(errorCode))
            {
                await request.SetBackendIdentityAsync(
                    null,
                    request.CancellationToken).ConfigureAwait(true);
            }

            return Aria2TransferFailureClassifier.Classify(
                errorCode,
                errorMessage);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC polling failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC polling contract failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-contract");
        }
        finally
        {
            ariaManager.TellStatus -= progressHandler;
            if (lastProgress != null)
            {
                await request.PersistProgressAsync(lastProgress, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
    }

    private async Task<AriaPauseCheckpoint> PauseAndWaitForCheckpointAsync(
        string gid,
        CancellationToken cancellationToken)
    {
        var pause = await _ariaClient.PauseAsync(gid, cancellationToken)
            .ConfigureAwait(true);
        if (pause is not { Result: { } pausedGid }
            || !string.Equals(pausedGid, gid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("aria2 rejected the pause request.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _ariaClient.TellStatus(gid, cancellationToken)
                .ConfigureAwait(true);
            if (status is not { Result: { } result })
            {
                throw new InvalidOperationException(
                    "aria2 rejected the pause status request.");
            }

            if (string.Equals(result.Status, "paused", StringComparison.Ordinal))
            {
                return AriaPauseCheckpoint.Paused;
            }

            if (string.Equals(result.Status, "complete", StringComparison.Ordinal)
                || string.Equals(result.Status, "error", StringComparison.Ordinal)
                || string.Equals(result.Status, "removed", StringComparison.Ordinal))
            {
                return AriaPauseCheckpoint.Terminal;
            }

            await Task.Delay(PauseStatusPollInterval, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private static async Task ObserveExpectedMonitorCancellationAsync(
        Task task,
        CancellationToken requestCancellationToken)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!requestCancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task<AriaTaskPreparation> EnsureAriaTaskAsync(
        DownloadTransferRequest request)
    {
        var gid = string.IsNullOrWhiteSpace(request.BackendIdentity)
            ? null
            : request.BackendIdentity;
        if (gid != null)
        {
            var status = await _ariaClient
                .TellStatus(gid, request.CancellationToken)
                .ConfigureAwait(true);
            if (status is not { Result: { } statusResult })
            {
                if (IsNotFound(status.Error))
                {
                    gid = null;
                    await request.SetBackendIdentityAsync(
                        null,
                        request.CancellationToken).ConfigureAwait(true);
                }
                else
                {
                    throw new InvalidOperationException(
                        "aria2 rejected the status request.");
                }
            }
            else
            {
                if (string.Equals(statusResult.Status, "paused", StringComparison.Ordinal))
                {
                    var taskAddress = await GetExistingTaskAddressAsync(
                        _ariaClient,
                        gid,
                        request.CancellationToken).ConfigureAwait(true);
                    var resumedTaskHeaders = AriaTaskHeaderPolicy.Create(
                        taskAddress,
                        _networkSettings.UserAgent,
                        LoginHelper.GetLoginInfoCookiesString());
                    await RefreshOptionsAndUnpauseAsync(
                        _ariaClient,
                        gid,
                        resumedTaskHeaders,
                        request.CancellationToken).ConfigureAwait(true);
                }

                return AriaTaskPreparation.Ready(gid);
            }
        }

        var resolution = await _addressResolver.ResolveAsync(
            request.Urls[0],
            _networkSettings.UserAgent,
            LoginHelper.GetLoginInfoCookiesString(),
            request.CancellationToken).ConfigureAwait(true);
        if (resolution.ErrorCode != null)
        {
            return AriaTaskPreparation.Rejected(resolution.ErrorCode);
        }

        var resolvedAddress = resolution.Address
            ?? throw new InvalidOperationException("The accepted aria2 address is missing.");
        var taskHeaders = resolution.Headers
            ?? throw new InvalidOperationException("The accepted aria2 task headers are missing.");
        var option = new AriaSendOption
        {
            Dir = request.Directory,
            Out = request.FileName,
            Continue = "true",
            AllowOverwrite = "true",
            AutoFileRenaming = "false",
            UserAgent = taskHeaders.UserAgent,
            Headers = taskHeaders.Headers,
            Split = _networkSettings.AriaSplit.ToString(CultureInfo.InvariantCulture),
            MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer
                .ToString(CultureInfo.InvariantCulture),
            MinSplitSize = $"{_networkSettings.AriaMinSplitSize}M",
            MaxTries = "1",
            RetryWait = "0",
            AlwaysResume = "false",
            MaxResumeFailureTries = "0"
        };
        if (_httpsProxyAddress != null)
        {
            option.HttpsProxy = _httpsProxyAddress.AbsoluteUri;
        }

        var added = await _ariaClient.AddUriAsync(
            [resolvedAddress.AbsoluteUri],
            option).ConfigureAwait(true);
        if (added is not { Result: { } addedGid } ||
            string.IsNullOrWhiteSpace(addedGid))
        {
            throw new InvalidOperationException(
                "aria2 rejected the addUri request.");
        }

        gid = addedGid;
        await request.SetBackendIdentityAsync(gid, request.CancellationToken).ConfigureAwait(true);
        return AriaTaskPreparation.Ready(gid);
    }

    private static async Task<string> GetExistingTaskAddressAsync(
        AriaClient ariaClient,
        string gid,
        CancellationToken cancellationToken)
    {
        var response = await ariaClient
            .GetUrisAsync(gid)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (response is not { Result: { Count: > 0 } uris })
        {
            throw new InvalidOperationException(
                "aria2 rejected the task address request.");
        }

        var current = uris.FirstOrDefault(uri =>
                string.Equals(uri.Status, "used", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(uri.Address))
            ?? uris.FirstOrDefault(uri => !string.IsNullOrWhiteSpace(uri.Address));
        return current?.Address
            ?? throw new InvalidOperationException(
                "aria2 returned no usable task address.");
    }

    internal static async Task RefreshOptionsAndUnpauseAsync(
        AriaClient ariaClient,
        string gid,
        AriaTaskHeaders taskHeaders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ariaClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(gid);
        ArgumentNullException.ThrowIfNull(taskHeaders);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["header"] = taskHeaders.Headers.ToArray(),
            ["user-agent"] = taskHeaders.UserAgent
        };
        var changed = await ariaClient
            .ChangeOptionAsync(gid, options)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (changed is not { Result: "OK" })
        {
            throw new InvalidOperationException(
                "aria2 rejected the task credential refresh.");
        }

        var unpaused = await ariaClient
            .UnpauseAsync(gid)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (unpaused is not { Result: { } unpausedGid }
            || string.IsNullOrWhiteSpace(unpausedGid))
        {
            throw new InvalidOperationException(
                "aria2 rejected the unpause request.");
        }
    }

    private static bool IsNotFound(AriaError? error)
    {
        return error?.Message.Contains(
            "is not found",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static Uri? ResolveHttpsProxyAddress(NetworkApplicationSettings settings)
    {
        if (settings.IsAriaHttpProxy != AllowStatus.Yes)
        {
            return null;
        }

        if (!AriaHttpsProxyPolicy.TryCreateConnectProxyUri(
                settings.AriaHttpProxy,
                settings.AriaHttpProxyListenPort,
                out var proxyAddress))
        {
            throw new InvalidOperationException(
                "The aria2 HTTPS download proxy must be a local HTTP CONNECT endpoint.");
        }

        return proxyAddress;
    }

    internal static bool ShouldClearBackendIdentity(string? errorCode)
    {
        return !string.IsNullOrWhiteSpace(errorCode) &&
               !errorCode.StartsWith("rpc-", StringComparison.Ordinal);
    }

    private DownloadProgress? CreateProgress(string activeGid, AriaProgressEventArgs eventArgs)
    {
        if (!string.Equals(activeGid, eventArgs.Gid, StringComparison.Ordinal))
        {
            return null;
        }

        var percent = eventArgs.TotalLength == 0
            ? 0
            : (float)eventArgs.CompletedLength / eventArgs.TotalLength * 100;
        _diagnosticLogger.LogSpeed(
            Name,
            eventArgs.Gid,
            eventArgs.CompletedLength,
            eventArgs.TotalLength,
            eventArgs.Speed);
        return new DownloadProgress(
            Math.Clamp(percent, 0, 100),
            eventArgs.CompletedLength,
            Math.Max(eventArgs.CompletedLength, eventArgs.TotalLength),
            eventArgs.Speed,
            $"{Format.FormatFileSize(eventArgs.CompletedLength)}/{Format.FormatFileSize(eventArgs.TotalLength)}",
            Format.FormatSpeedWithBandwidth(eventArgs.Speed));
    }

    public void Dispose()
    {
        try
        {
            _runtimeLifecycle.Dispose();
            ReleaseRuntimeRegistration();
        }
        finally
        {
            _addressResolver.Dispose();
        }
    }

    private void ReleaseRuntimeRegistration()
    {
        Interlocked.Exchange(ref _runtimeRegistration, null)?.Dispose();
    }

    private enum AriaPauseCheckpoint
    {
        Paused,
        Terminal
    }
}

internal sealed record AriaTaskPreparation(string? Gid, string? ErrorCode)
{
    public static AriaTaskPreparation Ready(string gid) => new(gid, ErrorCode: null);

    public static AriaTaskPreparation Rejected(string errorCode) => new(Gid: null, errorCode);
}
