using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;

namespace DownKyi.Presentation;

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
        set
        {
            if (IsUnavailable && value)
            {
                return;
            }

            SetProperty(ref isSelected, value);
        }
    }

    private bool _isUnavailable;

    public bool IsUnavailable
    {
        get => _isUnavailable;
        set
        {
            if (SetProperty(ref _isUnavailable, value) && value)
            {
                IsSelected = false;
            }
        }
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
    private RelayCommand? _titleCommand;

    public RelayCommand TitleCommand => _titleCommand ??= new RelayCommand(ExecuteTitleCommand);

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    private void ExecuteTitleCommand()
    {
        if (IsUnavailable)
        {
            return;
        }

        _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.VideoDetail,
            _parentRoute,
            $"{ParseEntrance.VideoUrl}{Bvid}"));
    }

    // 视频的UP主点击事件
    private RelayCommand? _videoUpperCommand;

    public RelayCommand VideoUpperCommand => _videoUpperCommand ??= new RelayCommand(ExecuteVideoUpperCommand);

    /// <summary>
    /// 视频的UP主点击事件
    /// </summary>
    private void ExecuteVideoUpperCommand()
    {
        var route = _settingsStore.Current.User.Mid == UpMid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        _navigationService.Navigate(new AppNavigationRequest(route, _parentRoute, UpMid));
    }

    #endregion
}
