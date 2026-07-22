using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewPublicFavoritesViewModel : ViewModelBase
{
    public const string Tag = "PagePublicFavorites";

    private readonly IClipboardService _clipboardService;
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly IFavoritesCoordinator _favoritesCoordinator;
    private readonly ILogger<ViewPublicFavoritesViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;

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

    private FavoritesPageItem _favorites = null!;

    public FavoritesPageItem Favorites
    {
        get => _favorites;
        set => SetProperty(ref _favorites, value);
    }

    private RangeObservableCollection<FavoritesMedia> _favoritesMedias = new();

    public RangeObservableCollection<FavoritesMedia> FavoritesMedias
    {
        get => _favoritesMedias;
        private set => SetProperty(ref _favoritesMedias, value);
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

    #endregion

    public ViewPublicFavoritesViewModel(
        IDesktopInteractionContext desktopInteractions,
        IClipboardService clipboardService,
        IContentDownloadCoordinator downloadCoordinator,
        IFavoritesCoordinator favoritesCoordinator,
        ISettingsStore settingsStore,
        ILogger<ViewPublicFavoritesViewModel> logger) : base(desktopInteractions)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _favoritesCoordinator = favoritesCoordinator ?? throw new ArgumentNullException(nameof(favoritesCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        // 初始化loading
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

        FavoritesMedias = new RangeObservableCollection<FavoritesMedia>();

        #endregion
    }

    #region 命令申明

    // 返回
    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
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
            AppRoute.PublicFavorites));
    }

    // 复制封面URL事件
    private DownKyiAsyncDelegateCommand? _copyCoverUrlCommand;

    public DownKyiAsyncDelegateCommand CopyCoverUrlCommand => _copyCoverUrlCommand ??= new DownKyiAsyncDelegateCommand(ExecuteCopyCoverUrlCommand, _logger);

    /// <summary>
    /// 复制封面URL事件
    /// </summary>
    private async Task ExecuteCopyCoverUrlCommand()
    {
        // 复制封面url到剪贴板
        await _clipboardService.SetTextAsync(Favorites.CoverUrl).ConfigureAwait(true);
        _logger.LogInformationMessage("Favorites cover URL copied to the clipboard.");
    }

    // 前往UP主页事件
    private RelayCommand? _upperCommand;
    public RelayCommand UpperCommand => _upperCommand ??= new RelayCommand(ExecuteUpperCommand);

    /// <summary>
    /// 前往UP主页事件
    /// </summary>
    private void ExecuteUpperCommand()
    {
        var route = _settingsStore.Current.User.Mid == Favorites.UpperMid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        Navigation.Navigate(new AppNavigationRequest(
            route,
            AppRoute.PublicFavorites,
            Favorites.UpperMid));
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

    private async Task AddToDownloadAsync(bool isOnlySelected)
    {
        var cancellationToken = ReplaceCancellationSource(ref _downloadCancellation);
        var items = FavoritesSelectionPolicy.CreateDownloadItems(FavoritesMedias);
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

    /// <summary>
    /// 初始化页面元素
    /// </summary>
    private void InitView()
    {
        _logger.LogDebugMessage("Initializing public favorites view.");

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        ContentVisibility = false;
        LoadingVisibility = false;
        NoDataVisibility = false;
        MediaLoadingVisibility = false;
        MediaNoDataVisibility = false;

        FavoritesMedias.Clear();
    }

    /// <summary>
    /// 接收收藏夹id参数
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);
        RunFireAndForget(OnNavigatedToAsync(navigationContext), nameof(OnNavigatedToAsync), _logger);
    }

    private async Task OnNavigatedToAsync(AppNavigationContext navigationContext)
    {
        try
        {
            // 根据传入参数不同执行不同任务
            var parameter = navigationContext.Parameters.GetValue<long>("Parameter");
            if (parameter == 0)
            {
                return;
            }

            InitView();
            LoadingVisibility = true;
            var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
            var snapshot = await _favoritesCoordinator
                .LoadPublicFavoritesAsync(parameter, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null)
            {
                _logger.LogDebugMessage("Favorites response was empty.");
                LoadingVisibility = false;
                NoDataVisibility = true;
                return;
            }

            Favorites = snapshot.Favorites;
            ContentVisibility = true;
            LoadingVisibility = false;
            MediaLoadingVisibility = false;
            if (snapshot.Medias.Count == 0)
            {
                MediaNoDataVisibility = true;
                return;
            }

            FavoritesMedias.AddRange(snapshot.Medias);
        }
        catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested != false)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Public favorites loading failed.", e);
        }
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
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
        }

        base.Dispose(disposing);
    }
}
