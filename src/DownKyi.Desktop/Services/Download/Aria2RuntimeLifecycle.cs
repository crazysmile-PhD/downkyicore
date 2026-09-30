using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class Aria2RuntimeLifecycle : IDisposable
{
    internal const string SecureRedirectFeature = "downkyi-secure-redirect-v2";
    private static readonly TimeSpan ShutdownCheckpointTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownCheckpointPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly AriaClient _ariaClient;
    private readonly AriaServer _ariaServer;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly LocalAriaRpcEndpoint? _localEndpoint;
    private readonly ILogger<Aria2RuntimeLifecycle> _logger;
    private readonly NetworkApplicationSettings _networkSettings;
    private readonly bool _ownsAriaServer;

    public Aria2RuntimeLifecycle(
        NetworkApplicationSettings networkSettings,
        AriaClient ariaClient,
        DownloadDiagnosticLogger diagnosticLogger,
        AriaServer ariaServer,
        ILogger<Aria2RuntimeLifecycle> logger,
        bool ownsAriaServer,
        LocalAriaRpcEndpoint? localEndpoint)
    {
        _networkSettings = networkSettings ?? throw new ArgumentNullException(nameof(networkSettings));
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ariaServer = ariaServer ?? throw new ArgumentNullException(nameof(ariaServer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsAriaServer = ownsAriaServer;
        _localEndpoint = localEndpoint;
        if (ownsAriaServer != (localEndpoint != null))
        {
            throw new ArgumentException(
                "A local aria2 endpoint is required only for an owned aria2 process.",
                nameof(localEndpoint));
        }
    }

    public string Name => _ownsAriaServer ? "aria2-local" : "aria2-custom";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _ownsAriaServer
            ? StartOwnedServerAsync(cancellationToken)
            : EnsureSecureRedirectFeatureAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _ownsAriaServer
            ? CloseOwnedServerAsync(cancellationToken)
            : Task.CompletedTask;
    }

    public void AbortStartup()
    {
        if (_ownsAriaServer)
        {
            _ariaServer.KillTrackedServer("aria2 runtime startup failed.");
        }
    }

    private async Task StartOwnedServerAsync(CancellationToken cancellationToken)
    {
        var endpoint = _localEndpoint
            ?? throw new InvalidOperationException("The local aria2 endpoint is missing.");
        var config = CreateServerConfig(endpoint);
        _diagnosticLogger.LogAriaServerConfig(Name, config, _networkSettings);

        var errors = new ConcurrentQueue<string>();
        await _ariaServer.StartServerAsync(config, output =>
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                errors.Enqueue(output);
            }
        }).ConfigureAwait(true);

        if (string.Join(Environment.NewLine, errors)
            .Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The local aria2 process reported a startup error.");
        }

        HttpRequestException? lastRpcError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_ariaServer.IsTrackedServerRunning())
            {
                throw new InvalidOperationException(
                    "The supervised aria2 process exited before RPC became ready.");
            }

            try
            {
                var version = await _ariaClient
                    .GetAriaVersionAsync(cancellationToken)
                    .ConfigureAwait(true);
                if (version is { Result: { } versionResult })
                {
                    EnsureSecureRedirectFeature(versionResult.EnabledFeatures);
                    _ariaServer.ReleaseStartupSecrets();
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastRpcError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException(
            "The local aria2 process did not accept RPC requests in time.",
            lastRpcError);
    }

    private AriaConfig CreateServerConfig(LocalAriaRpcEndpoint endpoint)
    {
        return new AriaConfig
        {
            ListenPort = endpoint.Port,
            Token = endpoint.Secret,
            LogLevel = _networkSettings.AriaLogLevel,
            MaxConcurrentDownloads = _networkSettings.MaxCurrentDownloads,
            MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer,
            Split = _networkSettings.AriaSplit,
            MinSplitSize = _networkSettings.AriaMinSplitSize,
            MaxOverallDownloadLimit = _networkSettings.AriaMaxOverallDownloadLimit * 1024L,
            MaxDownloadLimit = _networkSettings.AriaMaxDownloadLimit * 1024L,
            ContinueDownload = true,
            FileAllocation = _networkSettings.AriaFileAllocation
        };
    }

    private async Task EnsureSecureRedirectFeatureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = await _ariaClient
            .GetAriaVersionAsync(cancellationToken)
            .ConfigureAwait(true);
        if (version is not { Result: { } versionResult })
        {
            throw new InvalidOperationException(
                "The aria2 endpoint did not return its security capabilities.");
        }

        EnsureSecureRedirectFeature(versionResult.EnabledFeatures);
    }

    private static void EnsureSecureRedirectFeature(IReadOnlyList<string> features)
    {
        if (!features.Contains(SecureRedirectFeature, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The aria2 endpoint is incompatible with DownKyi downloads: " +
                "aria2.getVersion().enabledFeatures does not include the required " +
                $"'{SecureRedirectFeature}' capability. Standard aria2 and Motrix do not " +
                "provide this DownKyi-specific redirect protection.");
        }
    }

    private async Task CloseOwnedServerAsync(CancellationToken cancellationToken)
    {
        var sessionCheckpointSaved = false;
        try
        {
            var paused = await _ariaClient.PauseAllAsync()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(true);
            if (paused is not { Result: "OK" })
            {
                throw new InvalidOperationException(
                    "aria2 did not acknowledge the shutdown pause request.");
            }

            await WaitForPausedDownloadsAsync(cancellationToken).ConfigureAwait(true);

            var saved = await _ariaClient.SaveSessionAsync()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(true);
            if (saved is not { Result: "OK" })
            {
                throw new InvalidOperationException(
                    "aria2 did not commit the shutdown session checkpoint.");
            }

            sessionCheckpointSaved = true;
        }
        catch (Exception exception) when (exception is TimeoutException
            or HttpRequestException
            or IOException
            or InvalidOperationException
            or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Aria server shutdown checkpoint failed.", exception);
        }

        if (sessionCheckpointSaved && _ariaServer.TerminateAfterSessionCheckpoint())
        {
            return;
        }

        if (!await _ariaServer.CloseServerAsync(
                _ariaClient,
                TimeSpan.FromSeconds(3)).ConfigureAwait(true))
        {
            await _ariaServer.ForceCloseServerAsync(
                _ariaClient,
                TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
    }

    private async Task WaitForPausedDownloadsAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = ShutdownCheckpointTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "aria2 downloads did not reach the paused checkpoint before shutdown.");
            }

            var response = await _ariaClient.GetGlobalStatAsync()
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(true);
            if (response is not { Result: { } result }
                || !int.TryParse(
                    result.NumActive,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var activeDownloads)
                || activeDownloads < 0
                || !int.TryParse(
                    result.NumWaiting,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var waitingDownloads)
                || waitingDownloads < 0)
            {
                throw new InvalidOperationException(
                    "aria2 returned invalid download counts during shutdown.");
            }

            if (activeDownloads == 0)
            {
                remaining = ShutdownCheckpointTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        "aria2 downloads did not reach the paused checkpoint before shutdown.");
                }

                if (await AreWaitingDownloadsPausedAsync(
                        waitingDownloads,
                        remaining,
                        cancellationToken).ConfigureAwait(true))
                {
                    return;
                }
            }

            remaining = ShutdownCheckpointTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "aria2 downloads did not reach the paused checkpoint before shutdown.");
            }

            await Task.Delay(
                    remaining < ShutdownCheckpointPollInterval
                        ? remaining
                        : ShutdownCheckpointPollInterval,
                    cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task<bool> AreWaitingDownloadsPausedAsync(
        int waitingDownloads,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (waitingDownloads == 0)
        {
            return true;
        }

        var response = await _ariaClient
            .TellWaitingAsync(0, waitingDownloads, cancellationToken)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(true);
        return response is { Result: { } downloads }
            && downloads.Count == waitingDownloads
            && downloads.All(download => string.Equals(
                download.Status,
                "paused",
                StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (_ownsAriaServer)
        {
            _ariaServer.KillTrackedServer(
                "aria2 runtime disposed before graceful shutdown completed.");
        }
    }
}
