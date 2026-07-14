using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class CustomAriaDownloadService : AriaDownloadService
{
    public CustomAriaDownloadService(
        ImmutableObservableCollection<DownloadingItem> downloadingList,
        ImmutableObservableCollection<DownloadedItem> downloadedList,
        IDialogService? dialogService)
        : base(downloadingList, downloadedList, dialogService, ownsAriaServer: false)
    {
    }
}
