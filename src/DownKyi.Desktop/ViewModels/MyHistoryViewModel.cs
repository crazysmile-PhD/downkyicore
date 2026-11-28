using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Commands;
using DownKyi.Images;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class MyHistoryViewModel : ViewModelBase
{
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly ILogger<MyHistoryViewModel> _logger;
    private readonly IPersonalMediaCoordinator _personalMediaCoordinator;
    private readonly ISettingsStore _settingsStore;

    // 每页视频数量，暂时在此写死，以后在设置中增加选项
    private const int VideoNumberInPage = 30;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;

    public DownKyiAsyncCommandGate DownloadCommandGate { get; } = new();
    private bool _isLoadingPage;
    private bool _hasMoreHistory = true;
    private int _loadVersion;
    private long _mid;
    private bool _hasLoadedPage;
    private bool _resumeInterruptedPage;

    #region 页面属性申明

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

    private bool _contentVisibility;

    public bool ContentVisibility
    {
        get => _contentVisibility;
        set => SetProperty(ref _contentVisibility, value);
    }

    private RangeObservableCollection<HistoryMedia> _medias = new();

    public RangeObservableCollection<HistoryMedia> Medias
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

    public MyHistoryViewModel(
        IDesktopInteractionContext desktopInteractions,
        IContentDownloadCoordinator downloadCoordinator,
        IPersonalMediaCoordinator personalMediaCoordinator,
        ISettingsStore settingsStore,
        ILogger<MyHistoryViewModel> logger) : base(desktopInteractions)
    {
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _personalMediaCoordinator = personalMediaCoordinator
            ?? throw new ArgumentNullException(nameof(personalMediaCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        // 初始化loading
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        ArrowBack = NavigationIcon.CreateArrowBack();

        // 下载管理按钮
        DownloadManage = ButtonIcon.DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;

        Medias = new RangeObservableCollection<HistoryMedia>();

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
        CancelOperations();
        InitView();


        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    // 前往下载管理页面
    private RelayCommand? _downloadManagerCommand;

    public RelayCommand DownloadManagerCommand =>
        _downloadManagerCommand ??= new RelayCommand(ExecuteDownloadManagerCommand);

    /// <summary>
    /// 前往下载管理页面
    /// </summary>
    private void ExecuteDownloadManagerCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.DownloadManager,
            AppRoute.MyHistory));
    }

    // 全选按钮点击事件
    private RelayCommand<object>? _selectAllCommand;

    public RelayCommand<object> SelectAllCommand =>
        _selectAllCommand ??= RequiredParameterCommand.Create<object>(ExecuteSelectAllCommand);

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
    private RelayCommand<object>? _mediasCommand;

    public RelayCommand<object> MediasCommand =>
        _mediasCommand ??= RequiredParameterCommand.Create<object>(ExecuteMediasCommand);

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

        IsSelectAll = selectedMedia.Count == Medias.Count;
    }

    // 添加选中项到下载列表事件
    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddToDownloadCommand =>
        _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(
            () => AddToDownloadAsync(true),
            _logger,
            executionGate: DownloadCommandGate,
            executionRejected: ShowDownloadPreparationConflict);

    /// <summary>
    /// 添加选中项到下载列表事件
    /// </summary>
    // 添加所有视频到下载列表事件
    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand =>
        _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(
            () => AddToDownloadAsync(false),
            _logger,
            executionGate: DownloadCommandGate,
            executionRejected: ShowDownloadPreparationConflict);

    private RelayCommand? _cancelDownloadPreparationCommand;

    public RelayCommand CancelDownloadPreparationCommand =>
        _cancelDownloadPreparationCommand ??= new RelayCommand(() => _downloadCancellation?.Cancel());

    private void ShowDownloadPreparationConflict()
    {
        Notifications.Show(DictionaryResource.GetString("TipDownloadPreparationAlreadyRunning"));
    }

    private DownKyiAsyncDelegateCommand? _loadMoreCommand;

    public DownKyiAsyncDelegateCommand LoadMoreCommand =>
        _loadMoreCommand ??= new DownKyiAsyncDelegateCommand(ExecuteLoadMoreCommand, _logger);

    private long _nextMax;

    private long _nextViewAt;

    private async Task ExecuteLoadMoreCommand()
    {
        if (NoDataVisibility || _isLoadingPage || !_hasMoreHistory || _loadCancellation == null
            || !IsCurrentAccount)
        {
            return;
        }

        await LoadHistoryPageAsync(
            reset: false,
            Volatile.Read(ref _loadVersion),
            _loadCancellation.Token).ConfigureAwait(true);
    }
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
            .Where(media => media.Business is "archive" or "pgc")
            .Select(media => ContentDownloadItem.TryCreate(
                media.Url,
                media.IsSelected,
                media.Title))
            .OfType<ContentDownloadItem>()
            .ToArray();
        try
        {
            var result = await _downloadCoordinator.AddAsync(
                items,
                isOnlySelected,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == null)
            {
                return;
            }

            Notifications.Show(ContentDownloadNotificationFormatter.Format(result.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("History download preparation failed.", e);
            Notifications.Show(e.Message);
        }
        finally
        {
            ReleaseCancellationSource(ref _downloadCancellation, cancellationToken);
        }
    }

    private async Task LoadHistoryPageAsync(bool reset, int loadVersion, CancellationToken cancellationToken)
    {
        if (_isLoadingPage) return;
        _isLoadingPage = true;
        LoadingVisibility = true;
        NoDataVisibility = false;

        try
        {
            var result = await _personalMediaCoordinator.LoadHistoryPageAsync(
                _nextMax,
                _nextViewAt,
                VideoNumberInPage,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (loadVersion != Volatile.Read(ref _loadVersion) || !IsCurrentAccount)
            {
                return;
            }

            _hasMoreHistory = result.HasMore;

            if (reset)
            {
                Medias.ReplaceRange(result.Medias);
            }
            else
            {
                Medias.AddRange(result.Medias);
            }

            _nextMax = result.NextMax;
            _nextViewAt = result.NextViewAt;
            _hasLoadedPage = true;

            ContentVisibility = Medias.Count > 0;
            NoDataVisibility = Medias.Count == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested || loadVersion != Volatile.Read(ref _loadVersion)
                || !IsCurrentAccount)
            {
                return;
            }

            _logger.LogErrorMessage("History page loading failed.", e);
            if (reset)
            {
                NoDataVisibility = true;
            }
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                LoadingVisibility = false;
                _isLoadingPage = false;
            }
        }
    }

    /// <summary>
    /// 初始化页面数据
    /// </summary>
    private void InitView()
    {

        DownloadManage = ButtonIcon.DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;

        ContentVisibility = false;
        LoadingVisibility = false;
        NoDataVisibility = false;

        _nextMax = 0;
        _nextViewAt = 0;
        _hasMoreHistory = true;
        _hasLoadedPage = false;
        _resumeInterruptedPage = false;
        Medias.Clear();
        IsSelectAll = false;
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        DownloadManage = ButtonIcon.DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;

        CancelOperations();
        var user = _settingsStore.Current.User;
        var mid = user.IsLogin ? user.Mid : 0;
        if (mid != _mid)
        {
            InitView();
            _mid = mid;
        }

        if (_mid == 0) return;

        // Navigation history retains this instance. Renew reads without discarding committed pages.
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        if (!_hasLoadedPage || _resumeInterruptedPage)
        {
            _resumeInterruptedPage = false;
            RunFireAndForget(LoadHistoryPageAsync(!_hasLoadedPage, Volatile.Read(ref _loadVersion), cancellationToken),
                nameof(LoadHistoryPageAsync), _logger);
        }
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
        base.OnNavigatedFrom(navigationContext);
    }

    private bool IsCurrentAccount => _settingsStore.Current.User is { IsLogin: true } user && user.Mid == _mid;

    private void CancelOperations()
    {
        _resumeInterruptedPage |= _isLoadingPage;
        Interlocked.Increment(ref _loadVersion);
        CancelAndDispose(ref _loadCancellation);
        LoadingVisibility = false;
        _isLoadingPage = false;
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
