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
    private readonly DownloadStorageService _downloadStorageService;
    private readonly IAppDialogService _dialogService;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly FfmpegProcessor _ffmpegProcessor;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILoggerFactory _loggerFactory;

    public DownloadRuntimeFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IAppDialogService dialogService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService
            ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
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

        return transferBackend == null
            ? null
            : new DownloadOrchestrator(
                _downloadLists,
                _downloadStorageService,
                _dialogService,
                _uiDispatcher,
                _settingsStore,
                _diagnosticLogger,
                _ffmpegProcessor,
                transferBackend,
                _loggerFactory.CreateLogger<DownloadOrchestrator>());
    }
}
