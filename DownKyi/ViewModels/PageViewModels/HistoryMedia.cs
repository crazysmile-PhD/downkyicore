using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Images;

namespace DownKyi.ViewModels.PageViewModels;

internal class HistoryMedia : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public HistoryMedia(
        IAppNavigationService navigationService,
        AppRoute parentRoute,
        ISettingsStore settingsStore)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    // bvid
    public string Bvid { get; set; } = string.Empty;

    // 播放url
    public string Url { get; set; } = string.Empty;

    // UP主的mid
    public long UpMid { get; set; }

    // 类型
    public string Business { get; set; } = string.Empty;

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

    // 分P的标题
    private string _subTitle = string.Empty;

    public string SubTitle
    {
        get => _subTitle;
        set => SetProperty(ref _subTitle, value);
    }

    // 时长
    private long _duration;

    public long Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    // tag标签
    private string _tagName = string.Empty;

    public string TagName
    {
        get => _tagName;
        set => SetProperty(ref _tagName, value);
    }

    // new_desc 剧集或分P描述
    private string _partdesc = string.Empty;

    public string Partdesc
    {
        get => _partdesc;
        set => SetProperty(ref _partdesc, value);
    }

    // 观看进度
    private string _progress = string.Empty;

    public string Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    // 观看平台
    private VectorImage? _platform;

    public VectorImage? Platform
    {
        get => _platform;
        set => SetProperty(ref _platform, value);
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

    // 是否显示Partdesc
    private bool _partdescVisibility;

    public bool PartdescVisibility
    {
        get => _partdescVisibility;
        set => SetProperty(ref _partdescVisibility, value);
    }

    // 是否显示UP主信息和分区信息
    private bool _upAndTagVisibility;

    public bool UpAndTagVisibility
    {
        get => _upAndTagVisibility;
        set => SetProperty(ref _upAndTagVisibility, value);
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
        _navigationService.Navigate(new AppNavigationRequest(AppRoute.VideoDetail, _parentRoute, Url));
    }

    // UP主头像点击事件
    private RelayCommand<object>? _upCommand;

    public RelayCommand<object> UpCommand => _upCommand ??= RequiredParameterCommand.Create<object>(ExecuteUpCommand);

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
