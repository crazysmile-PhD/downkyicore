using Avalonia.Media.Imaging;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;

namespace DownKyi.ViewModels.PageViewModels;

internal class BangumiFollowMedia : BindableBase
{
    protected IEventAggregator EventAggregator { get; }

    public BangumiFollowMedia(IEventAggregator eventAggregator)
    {
        this.EventAggregator = eventAggregator;
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
    private DelegateCommand<object>? titleCommand;

    public DelegateCommand<object> TitleCommand =>
        titleCommand ?? (titleCommand = new DelegateCommand<object>(ExecuteTitleCommand));

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteTitleCommand(object parameter)
    {
        if (parameter is not string tag)
        {
            return;
        }

        NavigateToView.NavigationView(EventAggregator, ViewVideoDetailViewModel.Tag, tag,
            $"{ParseEntrance.BangumiMediaUrl}md{MediaId}");
    }

    #endregion
}
