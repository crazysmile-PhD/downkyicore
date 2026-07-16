using System;
using DownKyi.Application.Desktop;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.FFMpeg;
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
    private readonly AriaServer _ariaServer;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly IUserNotificationService _notificationService;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly FfmpegProcessor _ffmpegProcessor;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILoggerFactory _loggerFactory;

    public DownloadRuntimeFactory(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IUserNotificationService notificationService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _ariaServer = ariaServer ?? throw new ArgumentNullException(nameof(ariaServer));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IDownloadRuntime? Create()
    {
        var downloader = _settingsStore.Current.Network.Downloader;
        ITransferBackend? transferBackend = downloader switch
        {
            DownloaderSetting.BuiltIn => new BuiltinTransferBackend(
                _settingsStore,
                _diagnosticLogger,
                _loggerFactory.CreateLogger<BuiltinTransferBackend>()),
            DownloaderSetting.Aria => new Aria2TransferBackend(
                _settingsStore,
                _diagnosticLogger,
                _ariaServer,
                _loggerFactory,
                _loggerFactory.CreateLogger<Aria2TransferBackend>(),
                ownsAriaServer: true),
            DownloaderSetting.CustomAria => new Aria2TransferBackend(
                _settingsStore,
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

        var pipeline = new DownloadPipeline(
                _downloadLists,
                _projectionStore,
                _notificationService,
                _uiDispatcher,
                _settingsStore,
                _diagnosticLogger,
                _ffmpegProcessor,
                transferBackend,
                _loggerFactory.CreateLogger<DownloadPipeline>());
        return new DownloadOrchestrator(
            pipeline,
            _downloadLists,
            _settingsStore,
            _loggerFactory.CreateLogger<DownloadOrchestrator>());
    }
}
