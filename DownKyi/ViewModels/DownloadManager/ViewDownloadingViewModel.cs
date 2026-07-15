using System;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Prism.Dialogs;
using Prism.Events;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class ViewDownloadingViewModel : ViewModelBase
    {
        public const string Tag = "PageDownloadManagerDownloading";

        private readonly IDownloadManagerCoordinator _downloadManagerCoordinator;
        private readonly ILogger<ViewDownloadingViewModel> _logger;

        #region 页面属性申明

        private ImmutableObservableCollection<DownloadingItem> _downloadingList = new();

        public ImmutableObservableCollection<DownloadingItem> DownloadingList
        {
            get => _downloadingList;
            private set => SetProperty(ref _downloadingList, value);
        }

        #endregion

        public ViewDownloadingViewModel(
            IEventAggregator eventAggregator,
            IDialogService dialogService,
            DownloadListState downloadLists,
            IDownloadManagerCoordinator downloadManagerCoordinator,
            ILogger<ViewDownloadingViewModel> logger) : base(
            eventAggregator, dialogService)
        {
            _downloadManagerCoordinator = downloadManagerCoordinator
                ?? throw new ArgumentNullException(nameof(downloadManagerCoordinator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // 初始化DownloadingList
            DownloadingList = (downloadLists ?? throw new ArgumentNullException(nameof(downloadLists))).Downloading;
        }

        #region 命令申明

        // 暂停所有下载事件
        private DownKyiAsyncDelegateCommand? _pauseAllDownloadingCommand;

        public DownKyiAsyncDelegateCommand PauseAllDownloadingCommand =>
            _pauseAllDownloadingCommand ??= new DownKyiAsyncDelegateCommand(
                ExecutePauseAllDownloadingCommand,
                _logger);

        /// <summary>
        /// 暂停所有下载事件
        /// </summary>
        private Task ExecutePauseAllDownloadingCommand()
        {
            return _downloadManagerCoordinator.PauseAllAsync(DownloadingList);
        }

        // 继续所有下载事件
        private DownKyiAsyncDelegateCommand? _continueAllDownloadingCommand;

        public DownKyiAsyncDelegateCommand ContinueAllDownloadingCommand =>
            _continueAllDownloadingCommand ??= new DownKyiAsyncDelegateCommand(
                ExecuteContinueAllDownloadingCommand,
                _logger);

        /// <summary>
        /// 继续所有下载事件
        /// </summary>
        private Task ExecuteContinueAllDownloadingCommand()
        {
            return _downloadManagerCoordinator.ResumeAllAsync(DownloadingList);
        }

        private DownKyiAsyncDelegateCommand<DownloadingItem>? _toggleDownloadingCommand;

        public DownKyiAsyncDelegateCommand<DownloadingItem> ToggleDownloadingCommand =>
            _toggleDownloadingCommand ??= new DownKyiAsyncDelegateCommand<DownloadingItem>(
                ExecuteToggleDownloadingCommand,
                _logger);

        private Task ExecuteToggleDownloadingCommand(DownloadingItem? downloadingItem)
        {
            return downloadingItem == null
                ? Task.CompletedTask
                : _downloadManagerCoordinator.ToggleAsync(downloadingItem);
        }

        // 删除所有下载事件
        private DownKyiAsyncDelegateCommand? _deleteAllDownloadingCommand;

        public DownKyiAsyncDelegateCommand DeleteAllDownloadingCommand => _deleteAllDownloadingCommand ??= new DownKyiAsyncDelegateCommand(ExecuteDeleteAllDownloadingCommand, _logger);

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

            await _downloadManagerCoordinator.DeleteAllAsync(DownloadingList).ConfigureAwait(true);
        }


        // 下载列表删除事件
        private DownKyiAsyncDelegateCommand<DownloadingItem>? _deleteCommand;
        public DownKyiAsyncDelegateCommand<DownloadingItem> DeleteCommand => _deleteCommand ??= new DownKyiAsyncDelegateCommand<DownloadingItem>(ExecuteDeleteCommand, _logger);

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

            await _downloadManagerCoordinator.DeleteAsync(downloadingItem).ConfigureAwait(true);
        }

        #endregion
    }
}
