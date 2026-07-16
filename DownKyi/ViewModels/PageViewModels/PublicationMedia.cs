using System;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using Prism.Commands;
using Prism.Mvvm;

namespace DownKyi.ViewModels.PageViewModels;

internal class PublicationMedia : BindableBase
{
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public PublicationMedia(IAppNavigationService navigationService, AppRoute parentRoute)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
    }

    private string _coverUrl = string.Empty;

    public string CoverUrl
    {
        get => _coverUrl;
        set => SetProperty(ref _coverUrl, value);
    }

    public long Avid { get; set; }
    public string Bvid { get; set; } = string.Empty;

    #region 页面属性申明

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private Bitmap _cover = null!;

    public Bitmap Cover
    {
        get => _cover;
        set => SetProperty(ref _cover, value);
    }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _duration = string.Empty;

    public string Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    private string _playNumber = string.Empty;

    public string PlayNumber
    {
        get => _playNumber;
        set => SetProperty(ref _playNumber, value);
    }

    private string _createTime = string.Empty;

    public string CreateTime
    {
        get => _createTime;
        set => SetProperty(ref _createTime, value);
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

    #endregion
}
