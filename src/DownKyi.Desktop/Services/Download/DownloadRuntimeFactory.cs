using System;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using Microsoft.Extensions.Logging;
using DownloaderSetting = DownKyi.Core.Settings.Downloader;

namespace DownKyi.Services.Download;

internal interface IDownloadRuntimeFactory
{
    IDownloadRuntime? Create();
}

internal sealed class DownloadRuntimeFactory : IDownloadRuntimeFactory
{
    private readonly DownloadListState _downloadLists;
    private readonly AriaRuntimeClientRegistry _ariaClientRegistry;
    private readonly AriaServer _ariaServer;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly IUserNotificationService _notificationService;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly FfmpegProcessor _ffmpegProcessor;
    private readonly ISettingsStore _settingsStore;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBilibiliApiClient _client;

    public DownloadRuntimeFactory(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskApplicationService tasks,
        IUserNotificationService notificationService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        IWbiKeyProvider wbiKeyProvider,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        AriaRuntimeClientRegistry ariaClientRegistry,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory,
        IBilibiliApiClient client)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _ariaClientRegistry = ariaClientRegistry
            ?? throw new ArgumentNullException(nameof(ariaClientRegistry));
        _ariaServer = ariaServer ?? throw new ArgumentNullException(nameof(ariaServer));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IDownloadRuntime? Create()
    {
        var settingsSnapshot = _settingsStore.Current;
        var network = settingsSnapshot.Network;
        var downloader = network.Downloader;
        ITransferBackend? transferBackend = downloader switch
        {
            DownloaderSetting.BuiltIn => new BuiltinTransferBackend(
                _settingsStore,
                _diagnosticLogger,
                _loggerFactory.CreateLogger<BuiltinTransferBackend>()),
            DownloaderSetting.Aria => new Aria2TransferBackend(
                network,
                new AriaClient(listenPort: network.AriaListenPort),
                _ariaClientRegistry,
                _diagnosticLogger,
                _ariaServer,
                _loggerFactory,
                _loggerFactory.CreateLogger<Aria2TransferBackend>(),
                ownsAriaServer: true),
            DownloaderSetting.CustomAria => new Aria2TransferBackend(
                network,
                new AriaClient(network.AriaHost, network.AriaListenPort, network.AriaToken),
                _ariaClientRegistry,
                _diagnosticLogger,
                _ariaServer,
                _loggerFactory,
                _loggerFactory.CreateLogger<Aria2TransferBackend>(),
                ownsAriaServer: false),
            _ => null
        };

        if (transferBackend == null)
        {
            return null;
        }

        var artifactWriter = new DownloadArtifactWriter(
            _wbiKeyProvider,
            _stateWriter,
            _loggerFactory.CreateLogger<DownloadArtifactWriter>(),
            _client);
        var shutdownRecovery = new DownloadTaskShutdownRecovery(
            _tasks,
            _stateWriter);
        var presenter = new DownloadActivityPresenter(_stateWriter);
        var contextFactory = new DownloadExecutionContextFactory(
            _projectionStore,
            _settingsStore);
        var completionProjector = new DownloadCompletionProjector(
            _downloadLists,
            _uiDispatcher);
        var playbackResolver = new DownloadPlaybackResolver(
            _wbiKeyProvider,
            TimeProvider.System,
            _client);
        var transferCoordinator = new DownloadTransferCoordinator(
            transferBackend,
            new DownloadRetryPolicy(),
            TimeProvider.System,
            _loggerFactory.CreateLogger<DownloadTransferCoordinator>());
        IDownloadPipelineStage[] stages =
        [
            new ResolvePlaybackStage(
                _notificationService,
                presenter,
                playbackResolver,
                _loggerFactory.CreateLogger<ResolvePlaybackStage>()),
            new DownloadMediaStage(
                _projectionStore,
                _stateWriter,
                transferCoordinator,
                playbackResolver,
                _loggerFactory.CreateLogger<DownloadMediaStage>()),
            new DownloadArtifactsStage(artifactWriter),
            new MuxStage(
                presenter,
                _ffmpegProcessor,
                _stateWriter),
            new ValidateStage(),
            new FinalizeStage(
                _projectionStore,
                _stateWriter,
                completionProjector,
                TimeProvider.System)
        ];
        var pipeline = new DownloadPipeline(
                contextFactory,
                stages,
                _stateWriter,
                shutdownRecovery,
                transferBackend,
                _loggerFactory.CreateLogger<DownloadPipeline>());
        return new DownloadOrchestrator(
            pipeline,
            _stateWriter,
            _tasks,
            network.MaxCurrentDownloads,
            _loggerFactory.CreateLogger<DownloadOrchestrator>());
    }
}
