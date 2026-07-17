using System;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;
using Prism.Commands;
using Prism.Mvvm;

namespace DownKyi.ViewModels.PageViewModels;

internal class ToViewMedia : BindableBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public ToViewMedia(
        IAppNavigationService navigationService,
        AppRoute parentRoute,
        ISettingsStore settingsStore)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    // aid
    public long Aid { get; set; }

    // bvid
    public string Bvid { get; set; } = string.Empty;

    // UP主的mid
    public long UpMid { get; set; }

    #region 页面属性申明

    // 是否选中
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // 封面
    private string _cover = string.Empty;

    public string Cover
    {
        get => _cover;
        set => SetProperty(ref _cover, value);
    }

    // 视频标题
    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    // UP主的昵称
    private string _upName = string.Empty;

    public string UpName
    {
        get => _upName;
        set => SetProperty(ref _upName, value);
    }

    // UP主的头像
    private string _upHeader = string.Empty;

    public string UpHeader
    {
        get => _upHeader;
        set => SetProperty(ref _upHeader, value);
    }

    #endregion

    #region 命令申明

    // 视频标题点击事件
    private DelegateCommand<object>? _titleCommand;

    public DelegateCommand<object> TitleCommand => _titleCommand ??= new DelegateCommand<object>(ExecuteTitleCommand);

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteTitleCommand(object parameter)
    {
        _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.VideoDetail,
            _parentRoute,
            $"{ParseEntrance.VideoUrl}{Bvid}"));
    }

    // UP主头像点击事件
    private DelegateCommand<object>? _upCommand;

    public DelegateCommand<object> UpCommand => _upCommand ??= new DelegateCommand<object>(ExecuteUpCommand);

    /// <summary>
    /// UP主头像点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteUpCommand(object parameter)
    {
        var route = _settingsStore.Current.User.Mid == UpMid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        _navigationService.Navigate(new AppNavigationRequest(route, _parentRoute, UpMid));
    }

    #endregion
}
