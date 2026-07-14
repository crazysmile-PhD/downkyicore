using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class ViewDownloadingViewModel : ViewModelBase
    {
        public const string Tag = "PageDownloadManagerDownloading";

        private readonly DownloadStorageService _downloadStorageService;

        #region 页面属性申明

        private ImmutableObservableCollection<DownloadingItem> _downloadingList = new();

        public ImmutableObservableCollection<DownloadingItem> DownloadingList
        {
            get => _downloadingList;
            private set => SetProperty(ref _downloadingList, value);
        }

        #endregion

        public ViewDownloadingViewModel(IEventAggregator eventAggregator, IDialogService dialogService, DownloadStorageService downloadStorageService) : base(
            eventAggregator, dialogService)
        {
            _downloadStorageService = downloadStorageService;
            // 初始化DownloadingList
            DownloadingList = App.DownloadingList;
        }

        #region 命令申明

        // 暂停所有下载事件
        private DelegateCommand? _pauseAllDownloadingCommand;

        public DelegateCommand PauseAllDownloadingCommand => _pauseAllDownloadingCommand ??= new DelegateCommand(ExecutePauseAllDownloadingCommand);

        /// <summary>
        /// 暂停所有下载事件
        /// </summary>
        private void ExecutePauseAllDownloadingCommand()
        {
            foreach (var downloading in _downloadingList)
            {
                switch (downloading.Downloading.DownloadStatus)
                {
                    case DownloadStatus.NotStarted:
                    case DownloadStatus.WaitForDownload:
                        downloading.Downloading.DownloadStatus = DownloadStatus.Pause;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Pausing");
                        downloading.StartOrPause = ButtonIcon.Instance().Start;
                        downloading.StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");
                        break;
                    case DownloadStatus.PauseStarted:
                        break;
                    case DownloadStatus.Pause:
                        break;
                    //case DownloadStatus.PAUSE_TO_WAIT:
                    case DownloadStatus.Downloading:
                        downloading.Downloading.DownloadStatus = DownloadStatus.Pause;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Pausing");
                        downloading.StartOrPause = ButtonIcon.Instance().Start;
                        downloading.StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");
                        break;
                    case DownloadStatus.DownloadSucceed:
                        // 下载成功后会从下载列表中删除
                        // 不会出现此分支
                        break;
                    case DownloadStatus.DownloadFailed:
                        break;
                }
            }
        }

        // 继续所有下载事件
        private DelegateCommand? _continueAllDownloadingCommand;

        public DelegateCommand ContinueAllDownloadingCommand => _continueAllDownloadingCommand ??= new DelegateCommand(ExecuteContinueAllDownloadingCommand);

        /// <summary>
        /// 继续所有下载事件
        /// </summary>
        private void ExecuteContinueAllDownloadingCommand()
        {
            foreach (var downloading in _downloadingList)
            {
                switch (downloading.Downloading.DownloadStatus)
                {
                    case DownloadStatus.NotStarted:
                    case DownloadStatus.WaitForDownload:
                        downloading.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Waiting");
                        break;
                    case DownloadStatus.PauseStarted:
                        downloading.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Waiting");
                        break;
                    case DownloadStatus.Pause:
                        downloading.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Waiting");
                        break;
                    //case DownloadStatus.PAUSE_TO_WAIT:
                    //    break;
                    case DownloadStatus.Downloading:
                        break;
                    case DownloadStatus.DownloadSucceed:
                        // 下载成功后会从下载列表中删除
                        // 不会出现此分支
                        break;
                    case DownloadStatus.DownloadFailed:
                        downloading.Downloading.DownloadStatus = DownloadStatus.WaitForDownload;
                        downloading.DownloadStatusTitle = DictionaryResource.GetString("Waiting");
                        break;
                }

                downloading.StartOrPause = ButtonIcon.Instance().Pause;
                downloading.StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");
            }
        }

        // 删除所有下载事件
        private DownKyiAsyncDelegateCommand? _deleteAllDownloadingCommand;

        public DownKyiAsyncDelegateCommand DeleteAllDownloadingCommand => _deleteAllDownloadingCommand ??= new DownKyiAsyncDelegateCommand(ExecuteDeleteAllDownloadingCommand);

        /// <summary>
        /// 删除所有下载事件
        /// </summary>
        private async Task ExecuteDeleteAllDownloadingCommand()
        {
            var alertService = new AlertService(DialogService);
            var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete")).ConfigureAwait(true);
            if (result != ButtonResult.OK)
            {
                return;
            }

            // 使用Clear()不能触发NotifyCollectionChangedAction.Remove事件
            // 因此遍历删除
            // DownloadingList中元素被删除后不能继续遍历
            var list = DownloadingList.ToList();
            foreach (var item in list)
            {
                await DeleteDownloadingItemAsync(item).ConfigureAwait(true);
            }
        }


        // 下载列表删除事件
        private DownKyiAsyncDelegateCommand<DownloadingItem>? _deleteCommand;
        public DownKyiAsyncDelegateCommand<DownloadingItem> DeleteCommand => _deleteCommand ??= new DownKyiAsyncDelegateCommand<DownloadingItem>(ExecuteDeleteCommand);

        /// <summary>
        /// 下载列表删除事件
        /// </summary>
        private async Task ExecuteDeleteCommand(DownloadingItem? downloadingItem)
        {
            if (downloadingItem == null)
            {
                return;
            }

            var alertService = new AlertService(DialogService);
            var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete"), 2).ConfigureAwait(true);
            if (result != ButtonResult.OK)
            {
                return;
            }

            await DeleteDownloadingItemAsync(downloadingItem).ConfigureAwait(true);
        }

        private async Task DeleteDownloadingItemAsync(DownloadingItem downloadingItem)
        {
            downloadingItem.Downloading.DownloadStatus = DownloadStatus.Pause;
            App.PropertyChangeAsync(() => App.DownloadingList.Remove(downloadingItem));
            await DownloadTaskFileService.CancelActiveDownloadAsync(downloadingItem).ConfigureAwait(true);
            await _downloadStorageService.RemoveDownloadingAsync(downloadingItem, true).ConfigureAwait(true);
            await DownloadTaskFileService.DeleteGeneratedFilesAsync(downloadingItem).ConfigureAwait(true);
        }

        #endregion
    }
}
