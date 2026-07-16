using System;
using System.Collections.Generic;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.PageViewModels;
using Prism.Commands;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels;

internal class ViewDownloadManagerViewModel : ViewModelBase
{
    public const string Tag = "PageDownloadManager";

    #region 页面属性申明

    private IReadOnlyList<TabHeader> _tabHeaders = Array.Empty<TabHeader>();

    public IReadOnlyList<TabHeader> TabHeaders
    {
        get => _tabHeaders;
        set => SetProperty(ref _tabHeaders, value);
    }

    private int _selectTabId;

    public int SelectTabId
    {
        get => _selectTabId;
        set => SetProperty(ref _selectTabId, value);
    }

    #endregion

    public ViewDownloadManagerViewModel(IDesktopInteractionContext desktopInteractions)
        : base(desktopInteractions)
    {
        #region 属性初始化

        TabHeaders = new List<TabHeader>
        {
            new()
            {
                Id = 0, Image = NormalIcon.Instance().Downloading, Title = DictionaryResource.GetString("Downloading")
            },
            new()
            {
                Id = 1, Image = NormalIcon.Instance().DownloadFinished,
                Title = DictionaryResource.GetString("DownloadFinished")
            }
        };

        #endregion
    }

    #region 命令申明

    // 返回事件
    private DelegateCommand? _backSpaceCommand;

    public DelegateCommand BackSpaceCommand => _backSpaceCommand ??= new DelegateCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回事件
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        NavigateToParent();
    }

    // 左侧tab点击事件
    private DelegateCommand<object>? _leftTabHeadersCommand;

    public DelegateCommand<object> LeftTabHeadersCommand => _leftTabHeadersCommand ??= new DelegateCommand<object>(ExecuteLeftTabHeadersCommand);

    /// <summary>
    /// 左侧tab点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteLeftTabHeadersCommand(object parameter)
    {
        if (parameter is not TabHeader tabHeader)
        {
            return;
        }

        switch (tabHeader.Id)
        {
            case 0:
                Navigation.NavigateRegion(AppNavigationRegion.DownloadManager, AppRoute.Downloading);
                break;
            case 1:
                Navigation.NavigateRegion(AppNavigationRegion.DownloadManager, AppRoute.DownloadFinished);
                break;
            default:
                break;
        }
    }

    #endregion

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        //// 进入设置页面时显示的设置项
        SelectTabId = 0;

        PropertyChangeAsync(() =>
            Navigation.NavigateRegion(AppNavigationRegion.DownloadManager, AppRoute.Downloading));
    }
}
