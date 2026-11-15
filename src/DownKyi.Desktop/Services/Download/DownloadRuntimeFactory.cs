using System;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Platform;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal interface IDownloadRuntimeFactory
{
    IDownloadRuntime? Create();
}

internal sealed class DownloadRuntimeFactory : IDownloadRuntimeFactory
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadTransferArtifactInvalidator _artifactInvalidator;
    private readonly IDownloadTaskLookup _taskLookup;
    private readonly IDownloadTaskSchedulerService _taskScheduler;
    private readonly IUserNotificationService _notificationService;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly IFfmpegMediaMuxer _ffmpegProcessor;
    private readonly DownloadTaskFileService _fileService;
    private readonly ISettingsStore _settingsStore;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBilibiliApiClient _client;
    private readonly DownloadTaskStaging? _staging;

    public DownloadRuntimeFactory(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        DownloadTransferArtifactInvalidator artifactInvalidator,
        IDownloadTaskLookup taskLookup,
        IDownloadTaskSchedulerService taskScheduler,
        IUserNotificationService notificationService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        IWbiKeyProvider wbiKeyProvider,
        DownloadDiagnosticLogger diagnosticLogger,
        IFfmpegMediaMuxer ffmpegProcessor,
        DownloadTaskFileService fileService,
        ILoggerFactory loggerFactory,
        IBilibiliApiClient client,
        DownloadTaskStaging? staging = null)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _artifactInvalidator = artifactInvalidator
            ?? throw new ArgumentNullException(nameof(artifactInvalidator));
        _taskLookup = taskLookup ?? throw new ArgumentNullException(nameof(taskLookup));
        _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _staging = staging;
    }

    public IDownloadRuntime? Create()
    {
        var settingsSnapshot = _settingsStore.Current;
        var network = settingsSnapshot.Network;
        ITransferBackend transferBackend = new BuiltinTransferBackend(
            _settingsStore,
            _diagnosticLogger,
            _loggerFactory.CreateLogger<BuiltinTransferBackend>());

        var artifactWriter = new DownloadArtifactWriter(
            _wbiKeyProvider,
            _stateWriter,
            _loggerFactory.CreateLogger<DownloadArtifactWriter>(),
            _client);
        var shutdownRecovery = new DownloadTaskShutdownRecovery(
            _taskLookup,
            _stateWriter);
        var presenter = new DownloadActivityPresenter(_projectionStore, _stateWriter);
        var contextFactory = new DownloadExecutionContextFactory(
            _projectionStore,
            _settingsStore,
            _staging);
        var completionProjector = new DownloadCompletionProjector(
            _downloadLists,
            _projectionStore,
            _uiDispatcher);
        var playbackResolver = new DownloadPlaybackResolver(
            _wbiKeyProvider,
            TimeProvider.System,
            _client);
        var transferCoordinator = new DownloadTransferCoordinator(
            transferBackend,
            new DownloadRetryPolicy(),
            TimeProvider.System,
            _loggerFactory.CreateLogger<DownloadTransferCoordinator>(),
            isBandwidthLimited: () =>
            {
                var limits = _settingsStore.Current.Network;
                return limits.MaxOverallDownloadLimit > 0 || limits.MaxDownloadLimit > 0;
            });
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
                _artifactInvalidator,
                transferCoordinator,
                playbackResolver,
                presenter,
                _loggerFactory.CreateLogger<DownloadMediaStage>()),
            new DownloadArtifactsStage(artifactWriter, presenter),
            new MuxStage(
                presenter,
                _ffmpegProcessor,
                _stateWriter),
            new ValidateStage(),
            new FinalizeStage(
                _projectionStore,
                _stateWriter,
                completionProjector,
                _fileService,
                TimeProvider.System,
                _loggerFactory.CreateLogger<FinalizeStage>())
        ];
        var pipeline = new DownloadPipeline(
                contextFactory,
                stages,
                _stateWriter,
                _fileService,
                shutdownRecovery,
                _loggerFactory.CreateLogger<DownloadPipeline>());
        return new DownloadOrchestrator(
            pipeline,
            _stateWriter,
            _taskScheduler,
            network.MaxCurrentDownloads,
            _loggerFactory.CreateLogger<DownloadOrchestrator>());
    }

}
