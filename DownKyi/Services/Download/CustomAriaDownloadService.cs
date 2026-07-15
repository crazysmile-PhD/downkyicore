using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class CustomAriaDownloadService : AriaDownloadService
{
    public CustomAriaDownloadService(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IDialogService? dialogService,
        IUiDispatcher uiDispatcher,
        ISettingsStore settingsStore,
        DownloadDiagnosticLogger diagnosticLogger,
        FfmpegProcessor ffmpegProcessor,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory,
        ILogger<CustomAriaDownloadService> logger)
        : base(
            downloadLists,
            downloadStorageService,
            dialogService,
            uiDispatcher,
            settingsStore,
            diagnosticLogger,
            ffmpegProcessor,
            ariaServer,
            loggerFactory,
            logger,
            ownsAriaServer: false)
    {
    }
}
