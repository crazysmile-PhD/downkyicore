using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Utils;

namespace DownKyi.Presentation;

internal class PublicationMedia : ObservableObject
{
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;
    private readonly SeasonNavigationPayload? _season;

    public PublicationMedia(
        IAppNavigationService navigationService,
        AppRoute parentRoute,
        SeasonNavigationPayload? season = null)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
        _season = season;
    }

    public bool IsDirectVideo => _season == null;
    public bool IsCollection => _season != null;

    public int? CollectionVideoCount { get; init; }

    public string CollectionCountText => CollectionVideoCount.HasValue
        ? DictionaryResource.Format("CollectionVideoCount", "共 {0} 个视频", CollectionVideoCount.Value)
        : DictionaryResource.GetString("CollectionCountUnknown", "视频数未知");

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
        set => SetProperty(ref _isSelected, IsDirectVideo && value);
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
    private RelayCommand? _titleCommand;

    public RelayCommand TitleCommand => _titleCommand ??= new RelayCommand(ExecuteTitleCommand);

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    private void ExecuteTitleCommand()
    {
        if (_season != null)
        {
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.SeasonsSeries,
                _parentRoute,
                _season));
            return;
        }

        _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.VideoDetail,
            _parentRoute,
            $"{ParseEntrance.VideoUrl}{Bvid}"));
    }

    #endregion
}
