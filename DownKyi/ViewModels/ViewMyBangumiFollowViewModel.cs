using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels;

internal class ViewMyBangumiFollowViewModel : ViewModelBase
{
    public const string Tag = "PageMyBangumiFollow";
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly ILogger<ViewMyBangumiFollowViewModel> _logger;
    private readonly IUserSpacePageCoordinator _userSpaceCoordinator;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;

    private long _mid = -1;

    // 每页视频数量，暂时在此写死，以后在设置中增加选项
    private const int VideoNumberInPage = 15;

    #region 页面属性申明

    private string _pageName = Tag;

    public string PageName
    {
        get => _pageName;
        set => SetProperty(ref _pageName, value);
    }

    private VectorImage _arrowBack = null!;

    public VectorImage ArrowBack
    {
        get => _arrowBack;
        set => SetProperty(ref _arrowBack, value);
    }

    private VectorImage _downloadManage = null!;

    public VectorImage DownloadManage
    {
        get => _downloadManage;
        set => SetProperty(ref _downloadManage, value);
    }

    private RangeObservableCollection<TabHeader> _tabHeaders = new();

    public RangeObservableCollection<TabHeader> TabHeaders
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

    private bool _contentVisibility;

    public bool ContentVisibility
    {
        get => _contentVisibility;
        set => SetProperty(ref _contentVisibility, value);
    }

    private CustomPagerViewModel _pager = null!;

    public CustomPagerViewModel Pager
    {
        get => _pager;
        private set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_pager, value))
            {
                return;
            }

            if (_pager != null)
            {
                _pager.CurrentChanging -= OnCurrentChangedPager;
                _pager.CountChanged -= OnCountChangedPager;
            }

            _pager = value;
            RaisePropertyChanged(nameof(Pager));
            _pager.CurrentChanging += OnCurrentChangedPager;
            _pager.CountChanged += OnCountChangedPager;
        }
    }

    private RangeObservableCollection<BangumiFollowMedia> _medias = new();

    public RangeObservableCollection<BangumiFollowMedia> Medias
    {
        get => _medias;
        private set => SetProperty(ref _medias, value);
    }

    private bool _isSelectAll;

    public bool IsSelectAll
    {
        get => _isSelectAll;
        set => SetProperty(ref _isSelectAll, value);
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

    #endregion

    public ViewMyBangumiFollowViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IContentDownloadCoordinator downloadCoordinator,
        IUserSpacePageCoordinator userSpaceCoordinator,
        ILogger<ViewMyBangumiFollowViewModel> logger) : base(
        eventAggregator)
    {
        DialogService = dialogService;
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _userSpaceCoordinator = userSpaceCoordinator ?? throw new ArgumentNullException(nameof(userSpaceCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        // 初始化loading
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        ArrowBack = NavigationIcon.Instance().ArrowBack;
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        TabHeaders = new RangeObservableCollection<TabHeader>
        {
            new() { Id = (long)BangumiType.ANIME, Title = DictionaryResource.GetString("FollowAnime") },
            new() { Id = (long)BangumiType.EPISODE, Title = DictionaryResource.GetString("FollowMovie") }
        };

        Medias = new RangeObservableCollection<BangumiFollowMedia>();

        #endregion
    }

    #region 命令申明

    // 返回事件
    private DelegateCommand? _backSpaceCommand;

    public DelegateCommand BackSpaceCommand => _backSpaceCommand ??= new DelegateCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回事件
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        InitView();

        ArrowBack.Fill = DictionaryResource.GetColor("ColorText");

        // 结束任务
        CancelOperations();

        var parameter = new NavigationParam
        {
            ViewName = ParentView,
            ParentViewName = null,
            Parameter = null
        };
        EventAggregator.GetEvent<NavigationEvent>().Publish(parameter);
    }

    // 前往下载管理页面
    private DelegateCommand? _downloadManagerCommand;

    public DelegateCommand DownloadManagerCommand => _downloadManagerCommand ??= new DelegateCommand(ExecuteDownloadManagerCommand);

    /// <summary>
    /// 前往下载管理页面
    /// </summary>
    private void ExecuteDownloadManagerCommand()
    {
        var parameter = new NavigationParam
        {
            ViewName = ViewDownloadManagerViewModel.Tag,
            ParentViewName = Tag,
            Parameter = null
        };
        EventAggregator.GetEvent<NavigationEvent>().Publish(parameter);
    }

    // 顶部tab点击事件
    private DelegateCommand<object>? _tabHeadersCommand;

    public DelegateCommand<object> TabHeadersCommand => _tabHeadersCommand ??= new DelegateCommand<object>(ExecuteTabHeadersCommand, CanExecuteTabHeadersCommand);

    /// <summary>
    /// 顶部tab点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteTabHeadersCommand(object parameter)
    {
        if (parameter is not TabHeader tabHeader)
        {
            return;
        }

        // 顶部tab点击后，隐藏Content
        ContentVisibility = false;

        // 页面选择
        Pager = new CustomPagerViewModel(1, 1);
        Pager.Current = 1;
    }

    /// <summary>
    /// 顶部tab点击事件是否允许执行
    /// </summary>
    /// <param name="parameter"></param>
    /// <returns></returns>
    private bool CanExecuteTabHeadersCommand(object parameter)
    {
        return IsEnabled;
    }

    // 全选按钮点击事件
    private DelegateCommand<object>? _selectAllCommand;

    public DelegateCommand<object> SelectAllCommand => _selectAllCommand ??= new DelegateCommand<object>(ExecuteSelectAllCommand);

    /// <summary>
    /// 全选按钮点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteSelectAllCommand(object parameter)
    {
        if (IsSelectAll)
        {
            foreach (var item in Medias)
            {
                item.IsSelected = true;
            }
        }
        else
        {
            foreach (var item in Medias)
            {
                item.IsSelected = false;
            }
        }
    }

    // 列表选择事件
    private DelegateCommand<object>? _mediasCommand;

    public DelegateCommand<object> MediasCommand => _mediasCommand ??= new DelegateCommand<object>(ExecuteMediasCommand);

    /// <summary>
    /// 列表选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteMediasCommand(object parameter)
    {
        if (!(parameter is IList selectedMedia))
        {
            return;
        }

        if (selectedMedia.Count == Medias.Count)
        {
            IsSelectAll = true;
        }
        else
        {
            IsSelectAll = false;
        }
    }

    // 添加选中项到下载列表事件
    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddToDownloadCommand => _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(true), _logger);

    /// <summary>
    /// 添加选中项到下载列表事件
    /// </summary>
    // 添加所有视频到下载列表事件
    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand => _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false), _logger);

    /// <summary>
    /// 添加所有视频到下载列表事件
    /// </summary>
    #endregion

    /// <summary>
    /// 添加到下载
    /// </summary>
    /// <param name="isOnlySelected"></param>
    private async Task AddToDownloadAsync(bool isOnlySelected)
    {
        var cancellationToken = ReplaceCancellationSource(ref _downloadCancellation);
        var items = Medias
            .Select(media => new ContentDownloadItem(
                $"{ParseEntrance.BangumiMediaUrl}md{media.MediaId}",
                DownloadInfoKind.Bangumi,
                media.IsSelected))
            .ToArray();
        try
        {
            var addedCount = await _downloadCoordinator.AddAsync(
                items,
                isOnlySelected,
                DialogService,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (addedCount == null)
            {
                return;
            }

            EventAggregator.GetEvent<MessageEvent>().Publish(addedCount <= 0
                ? DictionaryResource.GetString("TipAddDownloadingZero")
                : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{addedCount}{DictionaryResource.GetString("TipAddDownloadingFinished2")}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Bangumi download preparation failed.", e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);
        }
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

        RunFireAndForget(UpdateBangumiMediaListAsync(((CustomPagerViewModel)sender!).ProposedCurrent), nameof(UpdateBangumiMediaListAsync), _logger);
    }

    private async Task UpdateBangumiMediaListAsync(int current)
    {
        Medias.Clear();
        IsSelectAll = false;

        LoadingVisibility = true;
        NoDataVisibility = false;

        // 是否正在获取数据
        // 在所有的退出分支中都需要设为true
        IsEnabled = false;

        var tab = TabHeaders[SelectTabId];
        var type = (BangumiType)tab.Id;
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        try
        {
            var page = await _userSpaceCoordinator.LoadBangumiFollowPageAsync(
                _mid,
                type,
                current,
                VideoNumberInPage,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            LoadingVisibility = false;
            Pager.Count = page.PageCount;
            if (page.Medias.Count == 0)
            {
                NoDataVisibility = true;
                return;
            }

            ContentVisibility = true;
            Medias.AddRange(page.Medias);
            NoDataVisibility = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            LoadingVisibility = false;
            NoDataVisibility = true;
            _logger.LogErrorMessage("Bangumi page loading failed.", e);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    /// <summary>
    /// 初始化页面数据
    /// </summary>
    private void InitView()
    {
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        ContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;

        Medias.Clear();
        IsSelectAll = false;
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 根据传入参数不同执行不同任务
        _mid = navigationContext.Parameters.GetValue<long>("Parameter");
        if (_mid == 0)
        {
            return;
        }

        InitView();

        // 初始选中项
        SelectTabId = 0;

        // 页面选择
        Pager = new CustomPagerViewModel(1, 1);
        Pager.Current = 1;
    }

    public override void OnNavigatedFrom(NavigationContext navigationContext)
    {
        CancelOperations();
        IsEnabled = true;
        LoadingVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        CancelAndDispose(ref _loadCancellation);
        CancelAndDispose(ref _downloadCancellation);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelOperations();
            if (_pager != null)
            {
                _pager.CurrentChanging -= OnCurrentChangedPager;
                _pager.CountChanged -= OnCountChangedPager;
            }
        }

        base.Dispose(disposing);
    }
}
