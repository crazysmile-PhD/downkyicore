using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal partial class ViewMyFavoritesViewModel : ViewModelBase
{
    public const string Tag = "PageMyFavorites";
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly IFavoritesCoordinator _favoritesCoordinator;
    private readonly ILogger<ViewMyFavoritesViewModel> _logger;
    private CancellationTokenSource? _folderLoadCancellation;
    private CancellationTokenSource? _mediaLoadCancellation;
    private CancellationTokenSource? _downloadCancellation;

    private long _mid = -1;

    // 每页视频数量，暂时在此写死，以后在设置中增加选项
    private const int VideoNumberInPage = 20;

    #region 页面属性申明

    private string _pageName = Tag;

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

    private bool _mediaLoading;

    public bool MediaLoading
    {
        get => _mediaLoading;
        set => SetProperty(ref _mediaLoading, value);
    }

    private bool _mediaContentVisibility;

    public bool MediaContentVisibility
    {
        get => _mediaContentVisibility;
        set => SetProperty(ref _mediaContentVisibility, value);
    }

    private bool _mediaLoadingVisibility;

    public bool MediaLoadingVisibility
    {
        get => _mediaLoadingVisibility;
        set => SetProperty(ref _mediaLoadingVisibility, value);
    }

    private bool _mediaNoDataVisibility;

    public bool MediaNoDataVisibility
    {
        get => _mediaNoDataVisibility;
        set => SetProperty(ref _mediaNoDataVisibility, value);
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
            OnPropertyChanged(nameof(Pager));
            value.CurrentChanging += OnCurrentChangedPager;
            value.CountChanged += OnCountChangedPager;
        }
    }

    private RangeObservableCollection<FavoritesMedia> _medias = new();

    public RangeObservableCollection<FavoritesMedia> Medias
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

    #endregion

    public ViewMyFavoritesViewModel(
        IDesktopInteractionContext desktopInteractions,
        IContentDownloadCoordinator downloadCoordinator,
        IFavoritesCoordinator favoritesCoordinator,
        ILogger<ViewMyFavoritesViewModel> logger) : base(desktopInteractions)
    {
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _favoritesCoordinator = favoritesCoordinator ?? throw new ArgumentNullException(nameof(favoritesCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        // 初始化loading gif
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        MediaLoading = true;
        MediaLoadingVisibility = false;
        MediaNoDataVisibility = false;

        ArrowBack = NavigationIcon.CreateArrowBack();
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        TabHeaders = new RangeObservableCollection<TabHeader>();
        Medias = new RangeObservableCollection<FavoritesMedia>();

        #endregion
    }

    #region 命令申明

    // 返回事件
    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回事件
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        InitView();

        ArrowBack.Fill = DictionaryResource.GetColor("ColorText");
        // 结束任务
        CancelOperations();

        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    // 前往下载管理页面
    private RelayCommand? _downloadManagerCommand;

    public RelayCommand DownloadManagerCommand => _downloadManagerCommand ??= new RelayCommand(ExecuteDownloadManagerCommand);

    /// <summary>
    /// 前往下载管理页面
    /// </summary>
    private void ExecuteDownloadManagerCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.DownloadManager,
            AppRoute.MyFavorites));
    }

    // 左侧tab点击事件
    private RelayCommand<object>? _leftTabHeadersCommand;

    public RelayCommand<object> LeftTabHeadersCommand => _leftTabHeadersCommand ??= RequiredParameterCommand.Create<object>(ExecuteLeftTabHeadersCommand, CanExecuteLeftTabHeadersCommand);

    /// <summary>
    /// 左侧tab点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteLeftTabHeadersCommand(object parameter)
    {
        if (_suppressTabSelection || parameter is not TabHeader tabHeader)
        {
            return;
        }

        SelectFavoritesFolder(tabHeader);
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

    // 全选按钮点击事件
    private RelayCommand<object>? _selectAllCommand;

    public RelayCommand<object> SelectAllCommand => _selectAllCommand ??= RequiredParameterCommand.Create<object>(ExecuteSelectAllCommand);

    /// <summary>
    /// 全选按钮点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteSelectAllCommand(object parameter)
    {
        FavoritesSelectionPolicy.SetAllAvailableSelected(Medias, IsSelectAll);
    }

    // 列表选择事件
    private RelayCommand<object>? _mediasCommand;

    public RelayCommand<object> MediasCommand => _mediasCommand ??= RequiredParameterCommand.Create<object>(ExecuteMediasCommand);

    /// <summary>
    /// 列表选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteMediasCommand(object parameter)
    {
        if (parameter is not IList selectedMedia)
        {
            return;
        }

        IsSelectAll = FavoritesSelectionPolicy.AreAllAvailableSelected(Medias);
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
        var items = FavoritesSelectionPolicy.CreateDownloadItems(Medias);
        try
        {
            var addedCount = await _downloadCoordinator.AddAsync(
                items,
                isOnlySelected,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (addedCount == null)
            {
                return;
            }

            Notifications.Show(addedCount <= 0
                ? DictionaryResource.GetString("TipAddDownloadingZero")
                : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{addedCount}{DictionaryResource.GetString("TipAddDownloadingFinished2")}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Favorites download preparation failed.", e);
            Notifications.Show(e.Message);
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

        RunFireAndForget(UpdateFavoritesMediaListAsync(((CustomPagerViewModel)sender!).ProposedCurrent), nameof(UpdateFavoritesMediaListAsync), _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
        IsEnabled = true;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        CancelAndDispose(ref _folderLoadCancellation);
        CancelAndDispose(ref _mediaLoadCancellation);
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
