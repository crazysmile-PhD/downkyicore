using System;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownloaderSetting = DownKyi.Core.Settings.Downloader;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.Services.Download;

internal interface IDownloadRuntimeFactory
{
    IDownloadService? Create();
}

internal sealed class DownloadRuntimeFactory : IDownloadRuntimeFactory
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadStorageService _downloadStorageService;
    private readonly IDialogService _dialogService;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _uiDispatcher;

    public DownloadRuntimeFactory(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IDialogService dialogService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _downloadStorageService = downloadStorageService
            ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
    }

    public IDownloadService? Create()
    {
        return _settingsStore.Settings.GetDownloader() switch
        {
            DownloaderSetting.BuiltIn => new BuiltinDownloadService(
                _downloadLists,
                _downloadStorageService,
                _dialogService,
                _uiDispatcher,
                _settingsStore,
                _diagnosticLogger),
            DownloaderSetting.Aria => new AriaDownloadService(
                _downloadLists,
                _downloadStorageService,
                _dialogService,
                _uiDispatcher,
                _settingsStore,
                _diagnosticLogger),
            DownloaderSetting.CustomAria => new CustomAriaDownloadService(
                _downloadLists,
                _downloadStorageService,
                _dialogService,
                _uiDispatcher,
                _settingsStore,
                _diagnosticLogger),
            _ => null
        };
    }
}
