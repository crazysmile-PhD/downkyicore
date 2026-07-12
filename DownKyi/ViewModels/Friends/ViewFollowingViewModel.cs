using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomControl;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.Friends;

internal class ViewFollowingViewModel : ViewModelBase
{
    public const string Tag = "PageFriendsFollowing";

    // mid
    private long _mid = -1;

    // 每页数量，暂时在此写死，以后在设置中增加选项
    private const int NumberInPage = 20;

    #region 页面属性申明

    private string _pageName = ViewFriendsViewModel.Tag;

    public string PageName
    {
        get => _pageName;
        set => SetProperty(ref _pageName, value);
    }

    private bool _contentVisibility;

    public bool ContentVisibility
    {
        get => _contentVisibility;
        set => SetProperty(ref _contentVisibility, value);
    }

    private bool _innerContentVisibility;

    public bool InnerContentVisibility
    {
        get => _innerContentVisibility;
        set => SetProperty(ref _innerContentVisibility, value);
    }

    private bool _loading;

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    private bool _loadingVisibility;

    public bool LoadingVisibility
    {
        get => _loadingVisibility;
        set => SetProperty(ref _loadingVisibility, value);
    }

    private bool _noDataVisibility;

    public bool NoDataVisibility
    {
        get => _noDataVisibility;
        set => SetProperty(ref _noDataVisibility, value);
    }

    private bool _contentLoading;

    public bool ContentLoading
    {
        get => _contentLoading;
        set => SetProperty(ref _contentLoading, value);
    }

    private bool _contentLoadingVisibility;

    public bool ContentLoadingVisibility
    {
        get => _contentLoadingVisibility;
        set => SetProperty(ref _contentLoadingVisibility, value);
    }

    private bool _contentNoDataVisibility;

    public bool ContentNoDataVisibility
    {
        get => _contentNoDataVisibility;
        set => SetProperty(ref _contentNoDataVisibility, value);
    }

    private ObservableCollection<TabHeader> _tabHeaders = new();

    public ObservableCollection<TabHeader> TabHeaders
    {
        get => _tabHeaders;
        private set => SetProperty(ref _tabHeaders, value);
    }

    private int _selectTabId;

    public int SelectTabId
    {
        get => _selectTabId;
        set => SetProperty(ref _selectTabId, value);
    }

    private bool _isEnabled = true;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private CustomPagerViewModel _pager = null!;

    public CustomPagerViewModel Pager
    {
        get => _pager;
        set => SetProperty(ref _pager, value);
    }

    private ObservableCollection<FriendInfo> _contents = new();

    public ObservableCollection<FriendInfo> Contents
    {
        get => _contents;
        private set => SetProperty(ref _contents, value);
    }

    #endregion

    public ViewFollowingViewModel(IEventAggregator eventAggregator) : base(eventAggregator)
    {
        #region 属性初始化

        // 初始化loading gif
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        ContentLoading = true;
        ContentLoadingVisibility = false;
        ContentNoDataVisibility = false;

        TabHeaders = new ObservableCollection<TabHeader>();
        Contents = new ObservableCollection<FriendInfo>();

        #endregion
    }

    #region 命令申明

    // 左侧tab点击事件
    private DelegateCommand<object>? _leftTabHeadersCommand;

    public DelegateCommand<object> LeftTabHeadersCommand => _leftTabHeadersCommand ??= new DelegateCommand<object>(ExecuteLeftTabHeadersCommand, CanExecuteLeftTabHeadersCommand);

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

        // 页面选择
        Pager = new CustomPagerViewModel(1, (int)Math.Ceiling(double.Parse(tabHeader.SubTitle, CultureInfo.CurrentCulture) / NumberInPage));
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
        Pager.Current = 1;
    }

    /// <summary>
    /// 左侧tab点击事件是否允许执行
    /// </summary>
    /// <param name="parameter"></param>
    /// <returns></returns>
    private bool CanExecuteLeftTabHeadersCommand(object parameter)
    {
        return IsEnabled;
    }

    #endregion

    /// <summary>
    /// 初始化页面数据
    /// </summary>
    private void InitView()
    {
        ContentVisibility = false;
        InnerContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;
        ContentLoadingVisibility = false;
        ContentNoDataVisibility = false;

        TabHeaders.Clear();
        Contents.Clear();
        SelectTabId = -1;
    }

    /// <summary>
    /// 初始化左侧列表
    /// </summary>
    private async Task InitLeftTable()
    {
        TabHeaders.Clear();

        var userInfo = SettingsManager.Instance.GetUserInfo();
        if (userInfo != null && userInfo.Mid == _mid)
        {
            // 用户的关系状态数
            UserRelationStat? relationStat = null;
            await Task.Run(() => { relationStat = UserStatus.GetUserRelationStat(_mid); }).ConfigureAwait(true);
            if (relationStat != null)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = -1,
                    Title = DictionaryResource.GetString("AllFollowing"),
                    SubTitle = relationStat.Following.ToString(CultureInfo.CurrentCulture)
                });
                TabHeaders.Add(new TabHeader
                {
                    Id = -2,
                    Title = DictionaryResource.GetString("WhisperFollowing"),
                    SubTitle = relationStat.Whisper.ToString(CultureInfo.CurrentCulture)
                });
            }

            // 用户的关注分组
            IReadOnlyList<FollowingGroup>? followingGroup = null;
            await Task.Run(() => { followingGroup = UserRelation.GetFollowingGroup(); }).ConfigureAwait(true);
            if (followingGroup != null)
            {
                foreach (var tag in followingGroup)
                {
                    TabHeaders.Add(new TabHeader { Id = tag.TagId, Title = tag.Name, SubTitle = tag.Count.ToString(CultureInfo.CurrentCulture) });
                }
            }
        }
        else
        {
            // 用户的关系状态数
            UserRelationStat? relationStat = null;
            await Task.Run(() => { relationStat = UserStatus.GetUserRelationStat(_mid); }).ConfigureAwait(true);
            if (relationStat != null)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = -1,
                    Title = DictionaryResource.GetString("AllFollowing"),
                    SubTitle = relationStat.Following.ToString(CultureInfo.CurrentCulture)
                });
            }
        }

        ContentVisibility = true;
        LoadingVisibility = false;
    }

    private void LoadContent(IReadOnlyList<RelationFollowInfo> contents)
    {
        InnerContentVisibility = true;
        ContentLoadingVisibility = false;
        ContentNoDataVisibility = false;
        foreach (var item in contents)
        {
            PropertyChangeAsync(() => { Contents.Add(new FriendInfo(EventAggregator) { Mid = item.Mid, Header = item.Face, Name = item.Name, Sign = item.Sign }); });
        }
    }

    private async Task<bool> LoadAllFollowings(int pn, int ps)
    {
        IReadOnlyList<RelationFollowInfo>? contents = null;
        await Task.Run(() =>
        {
            var data = UserRelation.GetFollowings(_mid, pn, ps);
            if (data != null && data.List != null && data.List.Count > 0)
            {
                contents = data.List;
            }

            if (contents == null)
            {
                return;
            }

            LoadContent(contents);
        }).ConfigureAwait(true);

        return contents != null;
    }

    private async Task<bool> LoadWhispers(int pn, int ps)
    {
        IReadOnlyList<RelationFollowInfo>? contents = null;
        await Task.Run(() =>
        {
            contents = UserRelation.GetWhispers(pn, ps);
            if (contents == null)
            {
                return;
            }

            LoadContent(contents);
        }).ConfigureAwait(true);

        return contents != null;
    }

    private async Task<bool> LoadFollowingGroupContent(long tagId, int pn, int ps)
    {
        IReadOnlyList<RelationFollowInfo>? contents = null;
        await Task.Run(() =>
        {
            contents = UserRelation.GetFollowingGroupContent(tagId, pn, ps);
            if (contents == null)
            {
                return;
            }

            LoadContent(contents);
        }).ConfigureAwait(true);

        return contents != null;
    }

    private async Task UpdateContentAsync(int current)
    {
        // 是否正在获取数据
        // 在所有的退出分支中都需要设为true
        IsEnabled = false;

        Contents.Clear();
        InnerContentVisibility = false;
        ContentLoadingVisibility = true;
        ContentNoDataVisibility = false;

        var tab = TabHeaders[SelectTabId];

        var isSucceed = tab.Id switch
        {
            -1 => await LoadAllFollowings(current, NumberInPage).ConfigureAwait(true),
            -2 => await LoadWhispers(current, NumberInPage).ConfigureAwait(true),
            _ => await LoadFollowingGroupContent(tab.Id, current, NumberInPage).ConfigureAwait(true)
        };

        if (isSucceed)
        {
            InnerContentVisibility = true;
            ContentLoadingVisibility = false;
            ContentNoDataVisibility = false;
        }
        else
        {
            InnerContentVisibility = false;
            ContentLoadingVisibility = false;
            ContentNoDataVisibility = true;
        }

        IsEnabled = true;
    }

    private void OnCountChangedPager(object? sender, EventArgs e)
    {
    }

    private void OnCurrentChangedPager(object? sender, CancelEventArgs e)
    {
        if (!IsEnabled)
        {
            e.Cancel = true;
            return;
        }

        RunFireAndForget(UpdateContentAsync(((CustomPagerViewModel)sender!).ProposedCurrent), nameof(UpdateContentAsync));
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        // 传入mid
        var parameter = navigationContext.Parameters.GetValue<long>("mid");
        if (parameter == 0)
        {
            return;
        }

        _mid = parameter;

        // 是否是从PageFriends的headerTable的item点击进入的
        // true表示加载PageFriends后第一次进入此页面
        // false表示从headerTable的item点击进入的
        var isFirst = navigationContext.Parameters.GetValue<bool>("isFirst");
        if (isFirst)
        {
            async Task Init()
            {
                InitView();
                // 初始化左侧列表
                await InitLeftTable().ConfigureAwait(true);
                // 进入页面时显示的设置项
                SelectTabId = 0;
            }

            Dispatcher.UIThread.InvokeAsync(Init);
        }
    }
}
