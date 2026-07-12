using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomControl;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.Friends;

internal class ViewFollowerViewModel : ViewModelBase
{
    public const string Tag = "PageFriendsFollower";

    // mid
    private long _mid = -1;

    // 每页数量，暂时在此写死，以后在设置中增加选项
    private const int NumberInPage = 20;

    private bool _isEnabled = true;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

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

    public ViewFollowerViewModel(IEventAggregator eventAggregator) : base(eventAggregator)
    {
        #region 属性初始化

        // 初始化loading
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        Contents = new ObservableCollection<FriendInfo>();

        #endregion
    }


    private void LoadContent(IReadOnlyList<RelationFollowInfo> contents)
    {
        ContentVisibility = true;
        LoadingVisibility = false;
        NoDataVisibility = false;
        foreach (var item in contents)
        {
            PropertyChangeAsync(() => { Contents.Add(new FriendInfo(EventAggregator) { Mid = item.Mid, Header = item.Face, Name = item.Name, Sign = item.Sign }); });
        }
    }

    private async Task UpdateContentAsync(int current)
    {
        // 是否正在获取数据
        // 在所有的退出分支中都需要设为true
        IsEnabled = false;

        Contents.Clear();
        ContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;

        RelationFollow? data = null;
        IReadOnlyList<RelationFollowInfo>? contents = null;
        await Task.Run(() =>
        {
            data = UserRelation.GetFollowers(_mid, current, NumberInPage);
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

        if (data == null || contents == null)
        {
            ContentVisibility = false;
            LoadingVisibility = false;
            NoDataVisibility = true;
        }
        else
        {
            var userInfo = SettingsManager.Instance.GetUserInfo();
            if (userInfo != null && userInfo.Mid == _mid)
            {
                Pager.Count = (int)Math.Ceiling((double)data.Total / NumberInPage);
            }
            else
            {
                var page = (int)Math.Ceiling((double)data.Total / NumberInPage);
                Pager.Count = page > 5 ? 5 : page;
            }

            ContentVisibility = true;
            LoadingVisibility = false;
            NoDataVisibility = false;
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
    /// 初始化页面数据
    /// </summary>
    private void InitView()
    {
        ContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;

        Contents.Clear();
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
        if (!isFirst) return;
        InitView();

        //UpdateContent(1);

        // 页面选择
        Pager = new CustomPagerViewModel(1, (int)Math.Ceiling((double)1 / NumberInPage));
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
        Pager.Current = 1;
    }
}
