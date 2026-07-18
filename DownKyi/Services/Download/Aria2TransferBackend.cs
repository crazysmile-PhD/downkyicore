using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class Aria2TransferBackend : ITransferBackend
{
    private readonly AriaClient _ariaClient;
    private readonly AriaRuntimeClientRegistry _clientRegistry;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly AriaServer _ariaServer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NetworkApplicationSettings _networkSettings;
    private readonly bool _ownsAriaServer;
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
        bool ownsAriaServer)
    {
        _networkSettings = networkSettings ?? throw new ArgumentNullException(nameof(networkSettings));
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _clientRegistry = clientRegistry ?? throw new ArgumentNullException(nameof(clientRegistry));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ariaServer = ariaServer ?? throw new ArgumentNullException(nameof(ariaServer));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsAriaServer = ownsAriaServer;
    }

    public string Name => _ownsAriaServer ? "aria2-local" : "aria2-custom";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtimeRegistration ??= _clientRegistry.Activate(_ariaClient);
        try
        {
            if (_ownsAriaServer)
            {
                await StartAriaServerAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch
        {
            if (_ownsAriaServer)
            {
                _ariaServer.KillTrackedServer("aria2 runtime startup failed.");
            }

            ReleaseRuntimeRegistration();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_ownsAriaServer)
            {
                await CloseAriaServerAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            ReleaseRuntimeRegistration();
        }
    }

    public async Task<DownloadTransferOutcome> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var downloading = request.Download;
        var activeGid = await EnsureAriaTaskAsync(
            request).ConfigureAwait(true);
        if (activeGid == null)
        {
            return DownloadTransferOutcome.Failed;
        }

        _diagnosticLogger.LogAriaTaskStart(Name, activeGid, request.Urls.Count);
        var ariaManager = new AriaManager(
            _ariaClient,
            _loggerFactory.CreateLogger<AriaManager>());
        EventHandler<AriaProgressEventArgs> progressHandler = (_, eventArgs) =>
            UpdateProgress(downloading, eventArgs);
        ariaManager.TellStatus += progressHandler;
        try
        {
            var result = await ariaManager.GetDownloadStatusAsync(
                activeGid,
                async cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    request.EnsureActive();
                    if (downloading.Downloading.DownloadStatus == DownloadStatus.Pause)
                    {
                        await _ariaClient.PauseAsync(activeGid).ConfigureAwait(false);
                        throw new OperationCanceledException("Download was paused.");
                    }
                },
                request.CancellationToken).ConfigureAwait(true);

            return result switch
            {
                DownloadResult.SUCCESS => DownloadTransferOutcome.Succeeded,
                DownloadResult.ABORT => DownloadTransferOutcome.Failed,
                _ => DownloadTransferOutcome.Failed
            };
        }
        finally
        {
            ariaManager.TellStatus -= progressHandler;
        }
    }

    private async Task<string?> EnsureAriaTaskAsync(DownloadTransferRequest request)
    {
        var downloading = request.Download;
        var gid = downloading.Downloading.Gid;
        if (!string.IsNullOrWhiteSpace(gid))
        {
            var status = await _ariaClient.TellStatus(gid).ConfigureAwait(true);
            if (status?.Result == null ||
                status.Error?.Message.Contains("is not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                gid = null;
                downloading.Downloading.Gid = null;
                await request.PersistStateAsync(request.CancellationToken).ConfigureAwait(true);
            }
        }

        if (gid == null)
        {
            var option = new AriaSendOption
            {
                Dir = request.Directory,
                Out = request.FileName,
                Continue = "true",
                AllowOverwrite = "true",
                AutoFileRenaming = "false",
                UserAgent = _networkSettings.UserAgent,
                Split = _networkSettings.AriaSplit.ToString(CultureInfo.InvariantCulture),
                MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer
                    .ToString(CultureInfo.InvariantCulture),
                MinSplitSize = $"{_networkSettings.AriaMinSplitSize}M"
            };
            if (_networkSettings.IsAriaHttpProxy == AllowStatus.Yes)
            {
                option.HttpProxy = $"http://{_networkSettings.AriaHttpProxy}:{_networkSettings.AriaHttpProxyListenPort}";
            }

            var added = await _ariaClient.AddUriAsync(request.Urls.ToList(), option).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(added?.Result))
            {
                return null;
            }

            gid = added.Result;
            downloading.Downloading.Gid = gid;
            await request.PersistStateAsync(request.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            await _ariaClient.UnpauseAsync(gid).ConfigureAwait(true);
        }

        return gid;
    }

    private async Task StartAriaServerAsync(CancellationToken cancellationToken)
    {
        var config = new AriaConfig
        {
            ListenPort = _networkSettings.AriaListenPort,
            Token = "downkyi",
            LogLevel = _networkSettings.AriaLogLevel,
            MaxConcurrentDownloads = _networkSettings.MaxCurrentDownloads,
            MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer,
            Split = _networkSettings.AriaSplit,
            MinSplitSize = _networkSettings.AriaMinSplitSize,
            MaxOverallDownloadLimit = _networkSettings.AriaMaxOverallDownloadLimit * 1024L,
            MaxDownloadLimit = _networkSettings.AriaMaxDownloadLimit * 1024L,
            ContinueDownload = true,
            FileAllocation = _networkSettings.AriaFileAllocation,
            Headers =
            [
                $"Cookie: {LoginHelper.GetLoginInfoCookiesString()}",
                "Origin: https://www.bilibili.com",
                "Referer: https://www.bilibili.com",
                $"User-Agent: {_networkSettings.UserAgent}"
            ]
        };
        _diagnosticLogger.LogAriaServerConfig(Name, config);

        var errors = new ConcurrentQueue<string>();
        await _ariaServer.StartServerAsync(config, output =>
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                errors.Enqueue(output);
            }
        }).ConfigureAwait(true);

        var message = string.Join(Environment.NewLine, errors);
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The local aria2 process reported a startup error.");
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _ariaClient.GetGlobalOptionAsync().ConfigureAwait(true) != null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException("The local aria2 process did not accept RPC requests in time.");
    }

    private async Task CloseAriaServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ariaClient.PauseAllAsync()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception e) when (e is TimeoutException or HttpRequestException or IOException
            or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Aria server shutdown failed.", e);
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

    private void UpdateProgress(DownKyi.ViewModels.DownloadManager.DownloadingItem video, AriaProgressEventArgs eventArgs)
    {
        if (!string.Equals(video.Downloading.Gid, eventArgs.Gid, StringComparison.Ordinal))
        {
            return;
        }

        var percent = eventArgs.TotalLength == 0
            ? 0
            : (float)eventArgs.CompletedLength / eventArgs.TotalLength * 100;
        if (Math.Abs(percent - video.Progress) < 0.01)
        {
            return;
        }

        video.Progress = percent;
        video.DownloadingFileSize = $"{Format.FormatFileSize(eventArgs.CompletedLength)}/{Format.FormatFileSize(eventArgs.TotalLength)}";
        video.SpeedDisplay = Format.FormatSpeedWithBandwidth(eventArgs.Speed);
        _diagnosticLogger.LogSpeed(
            Name,
            eventArgs.Gid,
            eventArgs.CompletedLength,
            eventArgs.TotalLength,
            eventArgs.Speed);
        video.Downloading.MaxSpeed = Math.Max(video.Downloading.MaxSpeed, eventArgs.Speed);
    }

    public void Dispose()
    {
        if (_ownsAriaServer)
        {
            _ariaServer.KillTrackedServer("aria2 runtime disposed before graceful shutdown completed.");
        }

        ReleaseRuntimeRegistration();
    }

    private void ReleaseRuntimeRegistration()
    {
        Interlocked.Exchange(ref _runtimeRegistration, null)?.Dispose();
    }
}
