using System;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.PageViewModels;

internal class BangumiFollowMedia : ObservableObject
{
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public BangumiFollowMedia(IAppNavigationService navigationService, AppRoute parentRoute)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
    }

    // media id
    public long MediaId { get; set; }

    // season id
    public long SeasonId { get; set; }

    #region 页面属性申明

    // 是否选中
    private bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    // 封面
    private string cover = string.Empty;

    public string Cover
    {
        get => cover;
        set => SetProperty(ref cover, value);
    }

    // 视频标题
    private string title = string.Empty;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    // 视频类型名称
    private string seasonTypeName = string.Empty;

    public string SeasonTypeName
    {
        get => seasonTypeName;
        set => SetProperty(ref seasonTypeName, value);
    }

    // 地区
    private string area = string.Empty;

    public string Area
    {
        get => area;
        set => SetProperty(ref area, value);
    }

    // 标记是否会员
    private string badge = string.Empty;

    public string Badge
    {
        get => badge;
        set => SetProperty(ref badge, value);
    }

    // 简介
    private string evaluate = string.Empty;

    public string Evaluate
    {
        get => evaluate;
        set => SetProperty(ref evaluate, value);
    }

    // 视频更新进度
    private string indexShow = string.Empty;

    public string IndexShow
    {
        get => indexShow;
        set => SetProperty(ref indexShow, value);
    }

    // 观看进度
    private string progress = string.Empty;

    public string Progress
    {
        get => progress;
        set => SetProperty(ref progress, value);
    }

    #endregion

    #region 命令申明

    // 视频标题点击事件
    private RelayCommand<object>? titleCommand;

    public RelayCommand<object> TitleCommand =>
        titleCommand ?? (titleCommand = RequiredParameterCommand.Create<object>(ExecuteTitleCommand));

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteTitleCommand(object parameter)
    {
        _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.VideoDetail,
            _parentRoute,
            $"{ParseEntrance.BangumiMediaUrl}md{MediaId}"));
    }

    #endregion
}
