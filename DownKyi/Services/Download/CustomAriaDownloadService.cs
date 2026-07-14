using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class CustomAriaDownloadService : AriaDownloadService
{
    public CustomAriaDownloadService(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IDialogService? dialogService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger)
        : base(
            downloadLists,
            downloadStorageService,
            dialogService,
            uiDispatcher,
            settingsStore,
            diagnosticLogger,
            ownsAriaServer: false)
    {
    }
}
