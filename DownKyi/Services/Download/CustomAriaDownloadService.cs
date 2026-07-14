using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class CustomAriaDownloadService : AriaDownloadService
{
    public CustomAriaDownloadService(
        DownloadListState downloadLists,
        DownloadStorageService downloadStorageService,
        IDialogService? dialogService)
        : base(downloadLists, downloadStorageService, dialogService, ownsAriaServer: false)
    {
    }
}
