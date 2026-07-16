using System;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.PageViewModels;

internal class FavoritesMedia : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public FavoritesMedia(
        IAppNavigationService navigationService,
        AppRoute parentRoute,
        ISettingsStore settingsStore)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public long Avid { get; set; }
    public string Bvid { get; set; } = string.Empty;
    public long UpMid { get; set; }

    #region 页面属性申明

    private bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    private int order;

    public int Order
    {
        get => order;
        set => SetProperty(ref order, value);
    }

    private string cover = string.Empty;

    public string Cover
    {
        get => cover;
        set => SetProperty(ref cover, value);
    }

    private string title = string.Empty;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    private string playNumber = string.Empty;

    public string PlayNumber
    {
        get => playNumber;
        set => SetProperty(ref playNumber, value);
    }

    private string danmakuNumber = string.Empty;

    public string DanmakuNumber
    {
        get => danmakuNumber;
        set => SetProperty(ref danmakuNumber, value);
    }

    private string favoriteNumber = string.Empty;

    public string FavoriteNumber
    {
        get => favoriteNumber;
        set => SetProperty(ref favoriteNumber, value);
    }

    private string duration = string.Empty;

    public string Duration
    {
        get => duration;
        set => SetProperty(ref duration, value);
    }

    private string upName = string.Empty;

    public string UpName
    {
        get => upName;
        set => SetProperty(ref upName, value);
    }

    private string createTime = string.Empty;

    public string CreateTime
    {
        get => createTime;
        set => SetProperty(ref createTime, value);
    }

    private string favTime = string.Empty;

    public string FavTime
    {
        get => favTime;
        set => SetProperty(ref favTime, value);
    }

    #endregion

    #region 命令申明

    // 视频标题点击事件
    private RelayCommand<object>? _titleCommand;

    public RelayCommand<object> TitleCommand => _titleCommand ??= RequiredParameterCommand.Create<object>(ExecuteTitleCommand);

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

    // 视频的UP主点击事件
    private RelayCommand<object>? _videoUpperCommand;

    public RelayCommand<object> VideoUpperCommand => _videoUpperCommand ??= RequiredParameterCommand.Create<object>(ExecuteVideoUpperCommand);

    /// <summary>
    /// 视频的UP主点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoUpperCommand(object parameter)
    {
        var route = _settingsStore.Current.User.Mid == UpMid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        _navigationService.Navigate(new AppNavigationRequest(route, _parentRoute, UpMid));
    }

    #endregion
}
