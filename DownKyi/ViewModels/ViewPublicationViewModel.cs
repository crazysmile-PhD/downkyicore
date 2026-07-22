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
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels
{
    internal partial class ViewPublicationViewModel : ViewModelBase
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
                OnPropertyChanged(nameof(Pager));
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
            IDesktopInteractionContext desktopInteractions,
            IContentDownloadCoordinator downloadCoordinator,
            IUserSpacePageCoordinator userSpaceCoordinator,
            ILogger<ViewPublicationViewModel> logger) : base(desktopInteractions)
        {
            _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
            _userSpaceCoordinator = userSpaceCoordinator ?? throw new ArgumentNullException(nameof(userSpaceCoordinator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            #region 属性初始化

            // 初始化loading
            Loading = true;
            LoadingVisibility = false;
            NoDataVisibility = false;

            _arrowBack = NavigationIcon.CreateArrowBack();
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

        private RelayCommand? _backSpaceCommand;

        public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

        protected internal override void ExecuteBackSpace()
        {
            ArrowBack.Fill = DictionaryResource.GetColor("ColorText");

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
                AppRoute.Publication));
        }

        // 左侧tab点击事件
        private RelayCommand<object>? _leftTabHeadersCommand;

        public RelayCommand<object> LeftTabHeadersCommand =>
            _leftTabHeadersCommand ??= RequiredParameterCommand.Create<object>(ExecuteLeftTabHeadersCommand, CanExecuteLeftTabHeadersCommand);

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

            SelectPublicationType(tabHeader);
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
                _logger.LogErrorMessage("Publication download preparation failed.", e);
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

            Medias.Clear();
            IsSelectAll = false;
            LoadingVisibility = true;
            NoDataVisibility = false;

            RunFireAndForget(
                UpdatePublicationAsync(((CustomPagerViewModel)sender!).ProposedCurrent),
                nameof(UpdatePublicationAsync),
                _logger);
        }

        public override void OnNavigatedFrom(AppNavigationContext navigationContext)
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
