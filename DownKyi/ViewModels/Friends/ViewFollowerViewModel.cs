using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.CustomControl;
using DownKyi.Services.Friends;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.Friends;

internal class ViewFollowerViewModel : ViewModelBase
{
    public const string Tag = "PageFriendsFollower";
    private readonly IFriendRelationCoordinator _friendRelationCoordinator;
    private readonly ILogger<ViewFollowerViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;

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

    private RangeObservableCollection<FriendInfo> _contents = new();

    public RangeObservableCollection<FriendInfo> Contents
    {
        get => _contents;
        private set => SetProperty(ref _contents, value);
    }

    #endregion

    public ViewFollowerViewModel(
        IEventAggregator eventAggregator,
        IFriendRelationCoordinator friendRelationCoordinator,
        ISettingsStore settingsStore,
        ILogger<ViewFollowerViewModel> logger) : base(eventAggregator)
    {
        _friendRelationCoordinator = friendRelationCoordinator
            ?? throw new ArgumentNullException(nameof(friendRelationCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // 初始化loading
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        Contents = new RangeObservableCollection<FriendInfo>();

        #endregion
    }


    private async Task UpdateContentAsync(int current)
    {
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        // 是否正在获取数据
        // 在所有的退出分支中都需要设为true
        IsEnabled = false;

        Contents.Clear();
        ContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;

        try
        {
            var data = await _friendRelationCoordinator
                .LoadFollowerPageAsync(_mid, current, NumberInPage, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadCancellation?.Token != cancellationToken)
            {
                return;
            }

            if (data?.List == null || data.List.Count == 0)
            {
                ContentVisibility = false;
                LoadingVisibility = false;
                NoDataVisibility = true;
                return;
            }

            Contents.AddRange(data.List.Select(item => new FriendInfo(EventAggregator)
            {
                Mid = item.Mid,
                Header = item.Face,
                Name = item.Name,
                Sign = item.Sign
            }));

            var userInfo = _settingsStore.Current.User;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Follower page loading failed.", e);
            if (_loadCancellation?.Token == cancellationToken)
            {
                ContentVisibility = false;
                LoadingVisibility = false;
                NoDataVisibility = true;
            }
        }
        finally
        {
            if (_loadCancellation?.Token == cancellationToken)
            {
                IsEnabled = true;
            }
        }
    }

    private void OnCountChangedPager(object? sender, EventArgs e)
    {
    }

    private void ReplacePager(CustomPagerViewModel pager)
    {
        if (Pager != null)
        {
            Pager.CurrentChanging -= OnCurrentChangedPager;
            Pager.CountChanged -= OnCountChangedPager;
        }

        Pager = pager;
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
    }

    private void OnCurrentChangedPager(object? sender, CancelEventArgs e)
    {
        if (!IsEnabled)
        {
            e.Cancel = true;
            return;
        }

        RunFireAndForget(UpdateContentAsync(((CustomPagerViewModel)sender!).ProposedCurrent), nameof(UpdateContentAsync), _logger);
    }

    /// <summary>
    /// 初始化页面数据
    /// </summary>
    private void InitView()
    {
        IsEnabled = true;
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
        ReplacePager(new CustomPagerViewModel(1, (int)Math.Ceiling((double)1 / NumberInPage)));
        Pager.Current = 1;
    }

    public override void OnNavigatedFrom(NavigationContext navigationContext)
    {
        CancelAndDispose(ref _loadCancellation);
        IsEnabled = true;
        LoadingVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _loadCancellation);
            if (Pager != null)
            {
                Pager.CurrentChanging -= OnCurrentChangedPager;
                Pager.CountChanged -= OnCountChangedPager;
            }
        }

        base.Dispose(disposing);
    }
}
