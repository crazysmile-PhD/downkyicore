using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class ViewDownloadingViewModel : ViewModelBase
    {
        public const string Tag = "PageDownloadManagerDownloading";

        private readonly IDownloadManagerCoordinator _downloadManagerCoordinator;
        private readonly ILogger<ViewDownloadingViewModel> _logger;

        #region 页面属性申明

        public ReadOnlyObservableCollection<DownloadingItem> DownloadingList { get; }

        #endregion

        public ViewDownloadingViewModel(
            IDesktopInteractionContext desktopInteractions,
            DownloadListState downloadLists,
            IDownloadManagerCoordinator downloadManagerCoordinator,
            ILogger<ViewDownloadingViewModel> logger) : base(desktopInteractions)
        {
            _downloadManagerCoordinator = downloadManagerCoordinator
                ?? throw new ArgumentNullException(nameof(downloadManagerCoordinator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // 初始化DownloadingList
            DownloadingList = (downloadLists ?? throw new ArgumentNullException(nameof(downloadLists))).Downloading;
        }

        #region 命令申明

        // 暂停所有下载事件
        private RelayCommand? _pauseAllDownloadingCommand;

        public RelayCommand PauseAllDownloadingCommand =>
            _pauseAllDownloadingCommand ??= new RelayCommand(ExecutePauseAllDownloadingCommand);

        /// <summary>
        /// 暂停所有下载事件
        /// </summary>
        private void ExecutePauseAllDownloadingCommand()
        {
            RunFireAndForget(
                _downloadManagerCoordinator.PauseAllAsync(DownloadingList),
                nameof(ExecutePauseAllDownloadingCommand),
                _logger);
        }

        // 继续所有下载事件
        private RelayCommand? _continueAllDownloadingCommand;

        public RelayCommand ContinueAllDownloadingCommand =>
            _continueAllDownloadingCommand ??= new RelayCommand(ExecuteContinueAllDownloadingCommand);

        /// <summary>
        /// 继续所有下载事件
        /// </summary>
        private void ExecuteContinueAllDownloadingCommand()
        {
            RunFireAndForget(
                _downloadManagerCoordinator.ResumeAllAsync(DownloadingList),
                nameof(ExecuteContinueAllDownloadingCommand),
                _logger);
        }

        private DownKyiAsyncDelegateCommand<DownloadingItem>? _toggleDownloadingCommand;

        public DownKyiAsyncDelegateCommand<DownloadingItem> ToggleDownloadingCommand =>
            _toggleDownloadingCommand ??= new DownKyiAsyncDelegateCommand<DownloadingItem>(
                ExecuteToggleDownloadingCommand,
                _logger);

        private async Task ExecuteToggleDownloadingCommand(DownloadingItem? downloadingItem)
        {
            if (downloadingItem == null)
            {
                return;
            }

            var initialStatus = downloadingItem.Downloading.DownloadStatus;
            var taskId = downloadingItem.Downloading.Id;
            var action = initialStatus is DownloadStatus.PauseStarted
                or DownloadStatus.Pause
                or DownloadStatus.DownloadFailed
                    ? "resume"
                    : "pause";
            var startedTimestamp = Stopwatch.GetTimestamp();
            _logger.LogInformationMessage(
                $"source=download-ui-timing; stage=command-enter; task={taskId}; " +
                $"action={action}; initialStatus={initialStatus:G}; " +
                $"monotonicTicks={startedTimestamp}; frequency={Stopwatch.Frequency}");

            await _downloadManagerCoordinator.ToggleAsync(downloadingItem).ConfigureAwait(true);

            var stateAppliedTimestamp = Stopwatch.GetTimestamp();
            var projectedStatus = downloadingItem.Downloading.DownloadStatus;
            _logger.LogInformationMessage(
                $"source=download-ui-timing; stage=projection-observed; task={taskId}; " +
                $"action={action}; projectedStatus={projectedStatus:G}; " +
                $"monotonicTicks={stateAppliedTimestamp}; " +
                $"elapsedMs={Stopwatch.GetElapsedTime(startedTimestamp, stateAppliedTimestamp).TotalMilliseconds:F3}");
        }

        // 删除所有下载事件
        private DownKyiAsyncDelegateCommand? _deleteAllDownloadingCommand;

        public DownKyiAsyncDelegateCommand DeleteAllDownloadingCommand => _deleteAllDownloadingCommand ??= new DownKyiAsyncDelegateCommand(ExecuteDeleteAllDownloadingCommand, _logger);

        /// <summary>
        /// 删除所有下载事件
        /// </summary>
        private async Task ExecuteDeleteAllDownloadingCommand()
        {
            var alertService = new AlertService(AppDialogs);
            var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete")).ConfigureAwait(true);
            if (result != AppDialogOutcome.Accepted)
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

            var alertService = new AlertService(AppDialogs);
            var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete"), 2).ConfigureAwait(true);
            if (result != AppDialogOutcome.Accepted)
            {
                return;
            }

            await _downloadManagerCoordinator.DeleteAsync(downloadingItem).ConfigureAwait(true);
        }

        #endregion
    }
}
