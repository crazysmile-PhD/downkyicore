using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels;

internal class ViewMyFavoritesViewModel : ViewModelBase
{
    public const string Tag = "PageMyFavorites";

    //private readonly IDialogService dialogService;

    private CancellationTokenSource? _tokenSource1;
    private CancellationTokenSource? _tokenSource2;

    private long _mid = -1;

    // 每页视频数量，暂时在此写死，以后在设置中增加选项
    private const int VideoNumberInPage = 20;

    #region 页面属性申明

    private string _pageName = Tag;
    private string _activeSearchText = string.Empty;

    public string PageName
    {
        get => _pageName;
        set => SetProperty(ref _pageName, value);
    }

    private string _inputSearchText = string.Empty;

    public string InputSearchText
    {
        get => _inputSearchText;
        set => SetProperty(ref _inputSearchText, value);
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

    private VectorImage _searchIcon = null!;

    public VectorImage SearchIcon
    {
        get => _searchIcon;
        set => SetProperty(ref _searchIcon, value);
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
        set => SetProperty(ref _pager, value);
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

    public ViewMyFavoritesViewModel(IEventAggregator eventAggregator, IDialogService dialogService) : base(eventAggregator)
    {
        DialogService = dialogService;

        #region 属性初始化

        // 初始化loading gif
        Loading = true;
        LoadingVisibility = false;
        NoDataVisibility = false;

        MediaLoading = true;
        MediaLoadingVisibility = false;
        MediaNoDataVisibility = false;

        ArrowBack = NavigationIcon.Instance().ArrowBack;
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        SearchIcon = ButtonIcon.Instance().GeneralSearch;
        SearchIcon.Fill = DictionaryResource.GetColor("ColorPrimary");

        TabHeaders = new RangeObservableCollection<TabHeader>();
        Medias = new RangeObservableCollection<FavoritesMedia>();

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
        _tokenSource1?.Cancel();
        _tokenSource2?.Cancel();

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

        // tab点击后，隐藏MediaContent
        MediaContentVisibility = false;
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;

        // 页面选择
        Pager = new CustomPagerViewModel(1, (int)Math.Ceiling(double.Parse(tabHeader.SubTitle, CultureInfo.CurrentCulture) / VideoNumberInPage));
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
        Pager.Current = 1;
    }

    private DelegateCommand? _searchCommand;

    public DelegateCommand SearchCommand => _searchCommand ??= new DelegateCommand(ExecuteSearchCommand);

    private void ExecuteSearchCommand()
    {
        if (!IsEnabled || SelectTabId < 0 || SelectTabId >= TabHeaders.Count)
        {
            return;
        }

        _activeSearchText = InputSearchText.Trim();
        RunFireAndForget(
            UpdateFavoritesMediaListAsync(1, true),
            nameof(UpdateFavoritesMediaListAsync));
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
        if (parameter is not IList selectedMedia)
        {
            return;
        }

        IsSelectAll = selectedMedia.Count == Medias.Count;
    }

    // 添加选中项到下载列表事件
    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddToDownloadCommand => _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(true));

    /// <summary>
    /// 添加选中项到下载列表事件
    /// </summary>
    // 添加所有视频到下载列表事件
    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand => _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false));

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
        // 收藏夹里只有视频
        var addToDownloadService = new AddToDownloadService(PlayStreamType.Video);

        // 选择文件夹
        var directory = await addToDownloadService.SetDirectory(DialogService).ConfigureAwait(true);

        // 视频计数
        var i = 0;
        await Task.Run(async () =>
        {
            // 为了避免执行其他操作时，
            // Medias变化导致的异常
            var list = Medias.ToList();

            // 添加到下载
            foreach (var media in list)
            {
                // 只下载选中项，跳过未选中项
                if (isOnlySelected && !media.IsSelected)
                {
                    continue;
                }

                // 开启服务
                var videoInfoService = new VideoInfoService(media.Bvid);

                addToDownloadService.SetVideoInfoService(videoInfoService);
                addToDownloadService.GetVideo();
                addToDownloadService.ParseVideo(videoInfoService);
                // 下载
                i += await addToDownloadService.AddToDownload(EventAggregator, DialogService, directory).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        if (directory == null)
        {
            return;
        }

        // 通知用户添加到下载列表的结果
        EventAggregator.GetEvent<MessageEvent>().Publish(i <= 0
            ? DictionaryResource.GetString("TipAddDownloadingZero")
            : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{i}{DictionaryResource.GetString("TipAddDownloadingFinished2")}");
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

        RunFireAndForget(UpdateFavoritesMediaListAsync(((CustomPagerViewModel)sender!).ProposedCurrent), nameof(UpdateFavoritesMediaListAsync));
    }

    private async Task UpdateFavoritesMediaListAsync(int current, bool updatePager = false)
    {
        try
        {
            Medias.Clear();
            IsSelectAll = false;

            MediaLoadingVisibility = true;
            MediaNoDataVisibility = false;

            // 是否正在获取数据
            // 在所有的退出分支中都需要设为true
            IsEnabled = false;

            var tab = TabHeaders[SelectTabId];
            var cancellationToken = ReplaceCancellationSource(ref _tokenSource2);

            var resource = await Task.Run(
                () => FavoritesResource.GetFavoritesMediaResource(
                    tab.Id,
                    current,
                    VideoNumberInPage,
                    _activeSearchText,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);

            if (updatePager)
            {
                ConfigurePager(resource?.Info.MediaCount ?? 0);
            }

            var medias = resource?.Medias;
            if (medias == null || medias.Count == 0)
            {
                MediaContentVisibility = true;
                MediaLoadingVisibility = false;
                MediaNoDataVisibility = true;
                return;
            }

            MediaContentVisibility = true;
            MediaLoadingVisibility = false;
            MediaNoDataVisibility = false;

            await Task.Run(() =>
            {
                var service = new FavoritesService();
                service.GetFavoritesMediaList(medias, Medias, EventAggregator, cancellationToken);
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(nameof(ViewMyFavoritesViewModel), e);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ConfigurePager(int itemCount)
    {
        Pager = new CustomPagerViewModel(
            1,
            Math.Max(1, (int)Math.Ceiling((double)itemCount / VideoNumberInPage)));
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
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
        LoadingVisibility = true;
        NoDataVisibility = false;
        MediaLoadingVisibility = false;
        MediaNoDataVisibility = false;
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;

        TabHeaders.Clear();
        Medias.Clear();
        SelectTabId = -1;
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
        RunFireAndForget(OnNavigatedToAsync(navigationContext), nameof(OnNavigatedToAsync));
    }

    private async Task OnNavigatedToAsync(NavigationContext navigationContext)
    {
        try
        {
            // 根据传入参数不同执行不同任务
            _mid = navigationContext.Parameters.GetValue<long>("Parameter");
            if (_mid == 0)
            {
                return;
            }

            InitView();
            var cancellationToken = ReplaceCancellationSource(ref _tokenSource1);

            await Task.Run(() =>
            {
                var service = new FavoritesService();
                service.GetCreatedFavorites(_mid, TabHeaders, cancellationToken);
                service.GetCollectedFavorites(_mid, TabHeaders, cancellationToken);
            }, cancellationToken).ConfigureAwait(true);

            if (TabHeaders.Count == 0)
            {
                ContentVisibility = false;
                LoadingVisibility = false;
                NoDataVisibility = true;

                return;
            }

            ContentVisibility = true;
            LoadingVisibility = false;
            NoDataVisibility = false;

            // 初始选中项
            SelectTabId = 0;

            // 页面选择
            Pager = new CustomPagerViewModel(1,
            (int)Math.Ceiling(double.Parse(TabHeaders[0].SubTitle, CultureInfo.CurrentCulture) / VideoNumberInPage));
            Pager.CurrentChanging += OnCurrentChangedPager;
            Pager.CountChanged += OnCountChangedPager;
            Pager.Current = 1;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(nameof(ViewMyFavoritesViewModel), e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _tokenSource1?.Cancel();
            _tokenSource1?.Dispose();
            _tokenSource1 = null;
            _tokenSource2?.Cancel();
            _tokenSource2?.Dispose();
            _tokenSource2 = null;
        }

        base.Dispose(disposing);
    }
}
