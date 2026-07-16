using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.Settings;

namespace DownKyi.ViewModels;

internal class ViewSettingsViewModel : ViewModelBase
{
    public const string Tag = "PageSettings";

    #region 页面属性申明

    private VectorImage _arrowBack = null!;

    public VectorImage ArrowBack
    {
        get => _arrowBack;
        set => SetProperty(ref _arrowBack, value);
    }

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

    public ViewSettingsViewModel(IDesktopInteractionContext desktopInteractions)
        : base(desktopInteractions)
    {
        ObserveRegion(AppNavigationRegion.Settings);
        #region 属性初始化

        ArrowBack = NavigationIcon.Instance().ArrowBack;
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        TabHeaders = new List<TabHeader>
        {
            new() { Id = 0, Title = DictionaryResource.GetString("Basic") },
            new() { Id = 1, Title = DictionaryResource.GetString("Network") },
            new() { Id = 2, Title = DictionaryResource.GetString("Video") },
            new() { Id = 3, Title = DictionaryResource.GetString("SettingDanmaku") },
            new() { Id = 4, Title = DictionaryResource.GetString("About") }
        };

        #endregion
    }

    #region 命令申明

    // 返回事件
    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回事件
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        NavigateToParent();
    }

    // 左侧tab点击事件
    private RelayCommand<object>? _leftTabHeadersCommand;

    public RelayCommand<object> LeftTabHeadersCommand => _leftTabHeadersCommand ??= RequiredParameterCommand.Create<object>(ExecuteLeftTabHeadersCommand);

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
                Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsBasic);
                break;
            case 1:
                Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsNetwork);
                break;
            case 2:
                Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsVideo);
                break;
            case 3:
                Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsDanmaku);
                break;
            case 4:
                Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsAbout);
                break;
        }
    }

    private RelayCommand? _loadedCommand;

    public RelayCommand LoadedCommand => _loadedCommand ??= new RelayCommand(ExecuteLoadedCommand);

    /// <summary>
    /// region加载完成事件
    /// </summary>
    private void ExecuteLoadedCommand()
    {
        Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsBasic);
    }

    #endregion

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        // 进入设置页面时显示的设置项
        SelectTabId = 0;

        PropertyChangeAsync(() =>
            Navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsBasic));

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
    }
}
