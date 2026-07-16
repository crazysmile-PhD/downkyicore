using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels.DownloadManager;

internal class ViewDownloadFinishedViewModel : ViewModelBase
{
    public const string Tag = "PageDownloadManagerDownloadFinished";

    private readonly IDownloadManagerCoordinator _downloadManagerCoordinator;
    private readonly DownloadListState _downloadLists;
    private readonly ILogger<ViewDownloadFinishedViewModel> _logger;
    private readonly ISettingsStore _settingsStore;

    #region 页面属性申明

    private ImmutableObservableCollection<DownloadedItem> _downloadedList = new();

    public ImmutableObservableCollection<DownloadedItem> DownloadedList
    {
        get => _downloadedList;
        private set => SetProperty(ref _downloadedList, value);
    }

    private int _finishedSortBy;

    public int FinishedSortBy
    {
        get => _finishedSortBy;
        set => SetProperty(ref _finishedSortBy, value);
    }

    #endregion

    public ViewDownloadFinishedViewModel(
        IDesktopInteractionContext desktopInteractions,
        DownloadListState downloadLists,
        ISettingsStore settingsStore,
        IDownloadManagerCoordinator downloadManagerCoordinator,
        ILogger<ViewDownloadFinishedViewModel> logger
    ) : base(desktopInteractions)
    {
        // 初始化DownloadedList
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _downloadManagerCoordinator = downloadManagerCoordinator
            ?? throw new ArgumentNullException(nameof(downloadManagerCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DownloadedList = downloadLists.Downloaded;

        var finishedSort = _settingsStore.Current.Basic.DownloadFinishedSort;
        FinishedSortBy = finishedSort switch
        {
            DownloadFinishedSort.DownloadAsc => 0,
            DownloadFinishedSort.DownloadDesc => 1,
            DownloadFinishedSort.Number => 2,
            _ => 0
        };
        _downloadLists.SortDownloaded(finishedSort);
    }

    #region 命令申明

    // 下载完成列表排序事件
    private RelayCommand<object>? _finishedSortCommand;
    public RelayCommand<object> FinishedSortCommand => _finishedSortCommand ??= RequiredParameterCommand.Create<object>(ExecuteFinishedSortCommand);

    /// <summary>
    /// 下载完成列表排序事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteFinishedSortCommand(object parameter)
    {
        if (parameter is not int index)
        {
            return;
        }

        switch (index)
        {
            case 0:
                _downloadLists.SortDownloaded(DownloadFinishedSort.DownloadAsc);
                // 更新设置
                SetDownloadFinishedSort(DownloadFinishedSort.DownloadAsc);
                break;
            case 1:
                _downloadLists.SortDownloaded(DownloadFinishedSort.DownloadDesc);
                // 更新设置
                SetDownloadFinishedSort(DownloadFinishedSort.DownloadDesc);
                break;
            case 2:
                _downloadLists.SortDownloaded(DownloadFinishedSort.Number);
                // 更新设置
                SetDownloadFinishedSort(DownloadFinishedSort.Number);
                break;
            default:
                _downloadLists.SortDownloaded(DownloadFinishedSort.DownloadAsc);
                // 更新设置
                SetDownloadFinishedSort(DownloadFinishedSort.DownloadAsc);
                break;
        }
    }

    private void SetDownloadFinishedSort(DownloadFinishedSort sort)
    {
        _settingsStore.Update(settings => settings with
        {
            Basic = settings.Basic with { DownloadFinishedSort = sort }
        });
    }

    // 清空下载完成列表事件
    private DownKyiAsyncDelegateCommand? _clearAllDownloadedCommand;
    public DownKyiAsyncDelegateCommand ClearAllDownloadedCommand => _clearAllDownloadedCommand ??= new DownKyiAsyncDelegateCommand(ExecuteClearAllDownloadedCommand, _logger);

    /// <summary>
    /// 清空下载完成列表事件
    /// </summary>
    private async Task ExecuteClearAllDownloadedCommand()
    {
        try
        {
            var alertService = new AlertService(AppDialogs);
            var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete")).ConfigureAwait(true);
            if (result != AppDialogOutcome.Accepted)
            {
                return;
            }


            // 使用Clear()不能触发NotifyCollectionChangedAction.Remove事件
            // 因此遍历删除
            // DownloadingList中元素被删除后不能继续遍历
            await _downloadManagerCoordinator.ClearDownloadedAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            var alertService = new AlertService(AppDialogs);
            await alertService.ShowError(e.Message).ConfigureAwait(true);
        }
    }

    // 打开视频事件
    private DownKyiAsyncDelegateCommand<DownloadedItem>? _openVideoCommand;
    public DownKyiAsyncDelegateCommand<DownloadedItem> OpenVideoCommand => _openVideoCommand ??= new DownKyiAsyncDelegateCommand<DownloadedItem>(ExecuteOpenVideoCommand, _logger);

    /// <summary>
    /// 打开视频事件
    /// </summary>
    private async Task ExecuteOpenVideoCommand(DownloadedItem? downloadedItem)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        var result = await _downloadManagerCoordinator.OpenVideoAsync(downloadedItem).ConfigureAwait(true);
        PublishOpenResult(result, "无法打开视频文件");
    }

    // 打开文件夹事件
    private DownKyiAsyncDelegateCommand<DownloadedItem>? _openFolderCommand;

    public DownKyiAsyncDelegateCommand<DownloadedItem> OpenFolderCommand => _openFolderCommand ??= new DownKyiAsyncDelegateCommand<DownloadedItem>(ExecuteOpenFolderCommand, _logger);


    /// <summary>
    /// 打开文件夹事件
    /// </summary>
    private async Task ExecuteOpenFolderCommand(DownloadedItem? downloadedItem)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        var result = await _downloadManagerCoordinator.OpenFolderAsync(downloadedItem).ConfigureAwait(true);
        PublishOpenResult(result, "无法打开文件夹");
    }

    // 删除事件
    private DownKyiAsyncDelegateCommand<DownloadedItem>? _removeVideoCommand;

    public DownKyiAsyncDelegateCommand<DownloadedItem> RemoveVideoCommand => _removeVideoCommand ??= new DownKyiAsyncDelegateCommand<DownloadedItem>(ExecuteRemoveVideoCommand, _logger);

    /// <summary>
    /// 删除事件
    /// </summary>
    private async Task ExecuteRemoveVideoCommand(DownloadedItem? downloadedItem)
    {
        if (downloadedItem == null)
        {
            return;
        }

        var alertService = new AlertService(AppDialogs);
        var result = await alertService.ShowWarning(DictionaryResource.GetString("ConfirmDelete"), 2).ConfigureAwait(true);
        if (result != AppDialogOutcome.Accepted)
        {
            return;
        }

        await _downloadManagerCoordinator.RemoveDownloadedAsync(downloadedItem).ConfigureAwait(true);
    }

    private void PublishOpenResult(DownloadArtifactOpenResult result, string failureMessage)
    {
        var message = result switch
        {
            DownloadArtifactOpenResult.Opened => null,
            DownloadArtifactOpenResult.NotFound => "没有找到视频文件，可能被删除或移动！",
            DownloadArtifactOpenResult.OpenFailed => failureMessage,
            _ => failureMessage
        };
        if (message != null)
        {
            Notifications.Show(message);
        }
    }

    #endregion
}
