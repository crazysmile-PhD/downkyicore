using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewMyHistoryViewModel : ViewModelBase
{
    public const string Tag = "PageMyHistory";
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly ILogger<ViewMyHistoryViewModel> _logger;
    private readonly IPersonalMediaCoordinator _personalMediaCoordinator;

    // 每页视频数量，暂时在此写死，以后在设置中增加选项
    private const int VideoNumberInPage = 30;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isLoadingPage;
    private bool _hasMoreHistory = true;
    private int _loadVersion;

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

    public ViewMyHistoryViewModel(
        IDesktopInteractionContext desktopInteractions,
        IContentDownloadCoordinator downloadCoordinator,
        IPersonalMediaCoordinator personalMediaCoordinator,
        ILogger<ViewMyHistoryViewModel> logger) : base(desktopInteractions)
    {
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _personalMediaCoordinator = personalMediaCoordinator
            ?? throw new ArgumentNullException(nameof(personalMediaCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        // 初始化loading
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        ArrowBack = NavigationIcon.CreateArrowBack();
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

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

        ArrowBack.Fill = DictionaryResource.GetColor("ColorText");

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
        _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(true), _logger);

    /// <summary>
    /// 添加选中项到下载列表事件
    /// </summary>
    // 添加所有视频到下载列表事件
    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand =>
        _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false), _logger);

    private DownKyiAsyncDelegateCommand? _loadMoreCommand;

    public DownKyiAsyncDelegateCommand LoadMoreCommand =>
        _loadMoreCommand ??= new DownKyiAsyncDelegateCommand(ExecuteLoadMoreCommand, _logger);

    private long _nextMax;

    private long _nextViewAt;

    private async Task ExecuteLoadMoreCommand()
    {
        if (NoDataVisibility || _isLoadingPage || !_hasMoreHistory || _loadCancellation == null)
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
            .Select(media => new ContentDownloadItem(
                media.Url,
                media.Business == "archive" ? DownloadInfoKind.Video : DownloadInfoKind.Bangumi,
                media.IsSelected))
            .ToArray();
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
            _logger.LogErrorMessage("History download preparation failed.", e);
            Notifications.Show(e.Message);
        }
    }

    private async Task UpdateHistoryMediaListAsync()
    {
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        _isLoadingPage = false;
        _nextMax = 0;
        _nextViewAt = 0;
        _hasMoreHistory = true;
        await LoadHistoryPageAsync(reset: true, loadVersion, cancellationToken).ConfigureAwait(true);
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
            if (loadVersion != Volatile.Read(ref _loadVersion))
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
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        ContentVisibility = false;
        LoadingVisibility = false;
        NoDataVisibility = false;

        _nextMax = 0;
        _nextViewAt = 0;
        _hasMoreHistory = true;
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

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 根据传入参数不同执行不同任务
        var mid = navigationContext.Parameters.GetValue<long>("Parameter");
        if (mid == 0)
        {
            IsSelectAll = false;
            foreach (var media in Medias)
            {
                media.IsSelected = false;
            }

            return;
        }

        InitView();

        RunFireAndForget(UpdateHistoryMediaListAsync(), nameof(UpdateHistoryMediaListAsync), _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
        LoadingVisibility = false;
        _isLoadingPage = false;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        Interlocked.Increment(ref _loadVersion);
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
