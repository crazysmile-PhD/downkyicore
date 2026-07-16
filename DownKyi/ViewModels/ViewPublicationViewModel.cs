using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels
{
    internal class ViewPublicationViewModel : ViewModelBase
    {
        public const string Tag = "PagePublication";
        private readonly IContentDownloadCoordinator _downloadCoordinator;
        private readonly ILogger<ViewPublicationViewModel> _logger;
        private readonly IUserSpacePageCoordinator _userSpaceCoordinator;
        private CancellationTokenSource? _loadCancellation;
        private CancellationTokenSource? _downloadCancellation;

        private long _mid = -1;

        // 每页视频数量，暂时在此写死，以后在设置中增加选项
        private const int VideoNumberInPage = 30;

        #region 页面属性申明

        private string _pageName = Tag;

        public string PageName
        {
            get => _pageName;
            set => SetProperty(ref _pageName, value);
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

        private VectorImage _arrowBack;

        public VectorImage ArrowBack
        {
            get => _arrowBack;
            set => SetProperty(ref _arrowBack, value);
        }

        private VectorImage _downloadManage;

        public VectorImage DownloadManage
        {
            get => _downloadManage;
            set => SetProperty(ref _downloadManage, value);
        }

        private RangeObservableCollection<TabHeader> _tabHeaders;

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

        private CustomPagerViewModel _pager;

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

                _pager.CurrentChanging -= OnCurrentChangedPager;
                _pager.CountChanged -= OnCountChangedPager;
                _pager = value;
                RaisePropertyChanged(nameof(Pager));
                _pager.CurrentChanging += OnCurrentChangedPager;
                _pager.CountChanged += OnCountChangedPager;
            }
        }

        private RangeObservableCollection<PublicationMedia> _medias;

        public RangeObservableCollection<PublicationMedia> Medias
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

        public ViewPublicationViewModel(
            IEventAggregator eventAggregator,
            IDialogService dialogService,
            IContentDownloadCoordinator downloadCoordinator,
            IUserSpacePageCoordinator userSpaceCoordinator,
            ILogger<ViewPublicationViewModel> logger) : base(
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

            _arrowBack = NavigationIcon.Instance().ArrowBack;
            _arrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

            // 下载管理按钮
            _downloadManage = ButtonIcon.Instance().DownloadManage;
            _downloadManage.Height = 24;
            _downloadManage.Width = 24;
            _downloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

            _tabHeaders = new RangeObservableCollection<TabHeader>();
            _medias = new RangeObservableCollection<PublicationMedia>();
            _pager = new CustomPagerViewModel(1, 1);
            _pager.CurrentChanging += OnCurrentChangedPager;
            _pager.CountChanged += OnCountChangedPager;

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

        // 左侧tab点击事件
        private DelegateCommand<object>? _leftTabHeadersCommand;

        public DelegateCommand<object> LeftTabHeadersCommand =>
            _leftTabHeadersCommand ??= new DelegateCommand<object>(ExecuteLeftTabHeadersCommand, CanExecuteLeftTabHeadersCommand);

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
            Pager = new CustomPagerViewModel(1, (int)Math.Ceiling(double.Parse(tabHeader.SubTitle, CultureInfo.CurrentCulture) / VideoNumberInPage));
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
                .Select(media => new ContentDownloadItem(media.Bvid, DownloadInfoKind.Video, media.IsSelected))
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
                _logger.LogErrorMessage("Publication download preparation failed.", e);
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

            Medias.Clear();
            IsSelectAll = false;
            LoadingVisibility = true;
            NoDataVisibility = false;

            RunFireAndForget(
                UpdatePublicationAsync(((CustomPagerViewModel)sender!).ProposedCurrent),
                nameof(UpdatePublicationAsync),
                _logger);
        }

        private async Task UpdatePublicationAsync(int current)
        {
            IsEnabled = false;
            var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
            var tab = TabHeaders[SelectTabId];
            try
            {
                var medias = await _userSpaceCoordinator.LoadPublicationPageAsync(
                    _mid,
                    current,
                    VideoNumberInPage,
                    tab.Id,
                    cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                LoadingVisibility = false;
                if (medias.Count == 0)
                {
                    NoDataVisibility = true;
                    return;
                }

                Medias.AddRange(medias);
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
                _logger.LogErrorMessage("Publication page loading failed.", e);
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

            DownloadManage = ButtonIcon.Instance().DownloadManage;
            DownloadManage.Height = 24;
            DownloadManage.Width = 24;
            DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

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

            // 根据传入参数不同执行不同任务
            var parameter = navigationContext.Parameters.GetValue<Dictionary<string, object>>("Parameter");
            if (parameter == null)
            {
                return;
            }

            InitView();

            _mid = (long)parameter["mid"];
            var tid = (int)parameter["tid"];
            var zones = (List<PublicationZone>)parameter["list"];

            foreach (var item in zones)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = item.Tid,
                    Title = item.Name,
                    SubTitle = item.Count.ToString(CultureInfo.CurrentCulture)
                });
            }

            // 初始选中项
            var selectTab = TabHeaders.FirstOrDefault(item => item.Id == tid);
            if (selectTab == null)
            {
                NoDataVisibility = true;
                return;
            }

            SelectTabId = TabHeaders.IndexOf(selectTab);

            // 页面选择
            Pager = new CustomPagerViewModel(1,
            (int)Math.Ceiling(double.Parse(selectTab.SubTitle, CultureInfo.CurrentCulture) / VideoNumberInPage));
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
                _pager.CurrentChanging -= OnCurrentChangedPager;
                _pager.CountChanged -= OnCountChangedPager;
            }

            base.Dispose(disposing);
        }
    }
}
