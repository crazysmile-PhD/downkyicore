using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomControl;
using DownKyi.Services.Friends;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.Friends;

internal class ViewFollowingViewModel : ViewModelBase
{
    public const string Tag = "PageFriendsFollowing";
    private readonly IFriendRelationCoordinator _friendRelationCoordinator;
    private readonly ILogger<ViewFollowingViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;

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

    private RangeObservableCollection<FriendInfo> _contents = new();

    public RangeObservableCollection<FriendInfo> Contents
    {
        get => _contents;
        private set => SetProperty(ref _contents, value);
    }

    #endregion

    public ViewFollowingViewModel(
        IEventAggregator eventAggregator,
        IFriendRelationCoordinator friendRelationCoordinator,
        ISettingsStore settingsStore,
        ILogger<ViewFollowingViewModel> logger) : base(eventAggregator)
    {
        _friendRelationCoordinator = friendRelationCoordinator
            ?? throw new ArgumentNullException(nameof(friendRelationCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // 初始化loading gif
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        ContentLoading = true;
        ContentLoadingVisibility = false;
        ContentNoDataVisibility = false;

        TabHeaders = new ObservableCollection<TabHeader>();
        Contents = new RangeObservableCollection<FriendInfo>();

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
        ReplacePager(new CustomPagerViewModel(
            1,
            (int)Math.Ceiling(double.Parse(tabHeader.SubTitle, CultureInfo.CurrentCulture) / NumberInPage)));
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
        IsEnabled = true;
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
    private async Task InitializeAsync()
    {
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        InitView();
        try
        {
            var userInfo = _settingsStore.Current.User;
            var isCurrentUser = userInfo != null && userInfo.Mid == _mid;
            var overview = await _friendRelationCoordinator
                .LoadFollowingOverviewAsync(_mid, isCurrentUser, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadCancellation?.Token != cancellationToken)
            {
                return;
            }

            if (overview.Relation != null)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = -1,
                    Title = DictionaryResource.GetString("AllFollowing"),
                    SubTitle = overview.Relation.Following.ToString(CultureInfo.CurrentCulture)
                });
                if (isCurrentUser)
                {
                    TabHeaders.Add(new TabHeader
                    {
                        Id = -2,
                        Title = DictionaryResource.GetString("WhisperFollowing"),
                        SubTitle = overview.Relation.Whisper.ToString(CultureInfo.CurrentCulture)
                    });
                }
            }

            foreach (var tag in overview.Groups)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = tag.TagId,
                    Title = tag.Name,
                    SubTitle = tag.Count.ToString(CultureInfo.CurrentCulture)
                });
            }

            ContentVisibility = true;
            LoadingVisibility = false;
            if (TabHeaders.Count > 0)
            {
                SelectTabId = 0;
            }
            else
            {
                NoDataVisibility = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Following page initialization failed.", e);
            if (_loadCancellation?.Token == cancellationToken)
            {
                ContentVisibility = false;
                LoadingVisibility = false;
                NoDataVisibility = true;
            }
        }
    }

    private async Task UpdateContentAsync(int current)
    {
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        // 是否正在获取数据
        // 在所有的退出分支中都需要设为true
        IsEnabled = false;

        Contents.Clear();
        InnerContentVisibility = false;
        ContentLoadingVisibility = true;
        ContentNoDataVisibility = false;

        try
        {
            var tab = TabHeaders[SelectTabId];
            var kind = tab.Id switch
            {
                -1 => FollowingListKind.All,
                -2 => FollowingListKind.Whisper,
                _ => FollowingListKind.Group
            };
            var contents = await _friendRelationCoordinator
                .LoadFollowingPageAsync(
                    _mid,
                    kind,
                    tab.Id,
                    current,
                    NumberInPage,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadCancellation?.Token != cancellationToken)
            {
                return;
            }

            if (contents.Count > 0)
            {
                Contents.AddRange(contents.Select(item => new FriendInfo(EventAggregator)
                {
                    Mid = item.Mid,
                    Header = item.Face,
                    Name = item.Name,
                    Sign = item.Sign
                }));
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Following page loading failed.", e);
            if (_loadCancellation?.Token == cancellationToken)
            {
                InnerContentVisibility = false;
                ContentLoadingVisibility = false;
                ContentNoDataVisibility = true;
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
            RunFireAndForget(InitializeAsync(), nameof(InitializeAsync), _logger);
        }
    }

    public override void OnNavigatedFrom(NavigationContext navigationContext)
    {
        CancelAndDispose(ref _loadCancellation);
        IsEnabled = true;
        LoadingVisibility = false;
        ContentLoadingVisibility = false;
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
