using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Utils;
using DownKyi.CustomControl;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.UserSpace;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels
{
    internal class ViewPublicationViewModel : ViewModelBase
    {
        public const string Tag = "PagePublication";

        private CancellationTokenSource? _tokenSource;

        private long _mid = -1;
        private bool _isUserVideoList;
        private string _activeSearchText = string.Empty;
        private IReadOnlyList<UserVideoListArchive>? _allUserVideoListArchives;
        private IReadOnlyList<UserVideoListArchive>? _userVideoSearchResults;
        private bool _preserveStateOnReturn;
        private bool _resetStateOnNavigationAway;

        // 每页视频数量，暂时在此写死，以后在设置中增加选项
        private const int VideoNumberInPage = 30;

        #region 页面属性申明

        private string _pageName = Tag;

        public string PageName
        {
            get => _pageName;
            set => SetProperty(ref _pageName, value);
        }

        private string _pageTitle = string.Empty;

        public string PageTitle
        {
            get => _pageTitle;
            set => SetProperty(ref _pageTitle, value);
        }

        private string _inputSearchText = string.Empty;

        public string InputSearchText
        {
            get => _inputSearchText;
            set => SetProperty(ref _inputSearchText, value);
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

        private VectorImage _searchIcon = null!;

        public VectorImage SearchIcon
        {
            get => _searchIcon;
            set => SetProperty(ref _searchIcon, value);
        }

        private ObservableCollection<TabHeader> _tabHeaders;

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

        private CustomPagerViewModel _pager;

        public CustomPagerViewModel Pager
        {
            get => _pager;
            set => SetProperty(ref _pager, value);
        }

        private ObservableCollection<PublicationMedia> _medias;

        public ObservableCollection<PublicationMedia> Medias
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

        public ViewPublicationViewModel(IEventAggregator eventAggregator, IDialogService dialogService) : base(
            eventAggregator)
        {
            DialogService = dialogService;

            #region 属性初始化

            // 初始化loading
            Loading = true;
            LoadingVisibility = false;
            NoDataVisibility = false;
            PageTitle = DictionaryResource.GetString("Publication");

            _arrowBack = NavigationIcon.Instance().ArrowBack;
            _arrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

            // 下载管理按钮
            _downloadManage = ButtonIcon.Instance().DownloadManage;
            _downloadManage.Height = 24;
            _downloadManage.Width = 24;
            _downloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

            SearchIcon = ButtonIcon.Instance().GeneralSearch;
            SearchIcon.Fill = DictionaryResource.GetColor("ColorPrimary");

            _tabHeaders = new ObservableCollection<TabHeader>();
            _medias = new ObservableCollection<PublicationMedia>();
            _pager = new CustomPagerViewModel(1, 1);

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
            _tokenSource?.Cancel();
            _tokenSource?.Dispose();
            _tokenSource = null;
            _resetStateOnNavigationAway = true;
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

            InputSearchText = string.Empty;
            _activeSearchText = string.Empty;
            _userVideoSearchResults = null;

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
            Medias.Clear();
            IsSelectAll = false;
            LoadingVisibility = true;
            NoDataVisibility = false;

            if (_isUserVideoList && !string.IsNullOrWhiteSpace(_activeSearchText))
            {
                RunFireAndForget(SearchUserVideoListAsync(), nameof(SearchUserVideoListAsync));
                return;
            }

            _userVideoSearchResults = null;
            RunFireAndForget(UpdatePublication(1, true), nameof(UpdatePublication));
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
                foreach (var videoInfoService in from media in list where !isOnlySelected || media.IsSelected select new VideoInfoService(media.Bvid))
                {
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

            Medias.Clear();
            IsSelectAll = false;
            LoadingVisibility = true;
            NoDataVisibility = false;

            _ = UpdatePublication(((CustomPagerViewModel)sender!).ProposedCurrent);
        }

        private static string StringToUnicode(string s)
        {
            var charbuffers = s.ToCharArray();
            byte[] buffer;
            var sb = new StringBuilder();
            foreach (var t in charbuffers)
            {
                buffer = Encoding.Unicode.GetBytes(t.ToString());
                sb.Append(CultureInfo.InvariantCulture, $"\\u{buffer[1]:X2}{buffer[0]:X2}");
            }

            return sb.ToString();
        }

        private readonly Bitmap _defaultPic = ImageHelper.LoadFromResource(new Uri("avares://DownKyi/Resources/video-placeholder.png"));

        private async Task UpdatePublication(int current, bool updatePager = false)
        {
            if (_tokenSource != null)
            {
                await _tokenSource.CancelAsync().ConfigureAwait(true);
            }
            // 是否正在获取数据
            // 在所有的退出分支中都需要设为true
            IsEnabled = false;
            _tokenSource = new CancellationTokenSource();
            var cancellationToken = _tokenSource.Token;

            var tab = TabHeaders[SelectTabId];
            try
            {
                await Task.Run(() =>
                {
                    if (_isUserVideoList)
                    {
                        if (_userVideoSearchResults != null)
                        {
                            var page = _userVideoSearchResults
                                .Skip((current - 1) * VideoNumberInPage)
                                .Take(VideoNumberInPage)
                                .ToList();
                            if (page.Count == 0)
                            {
                                LoadingVisibility = false;
                                NoDataVisibility = true;
                                return;
                            }

                            foreach (var video in page)
                            {
                                AddUserVideoListMedia(video);
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            return;
                        }

                        var userVideoList = Core.BiliApi.Users.UserSpace.GetUserVideoList(
                            _mid,
                            current,
                            VideoNumberInPage,
                            cancellationToken);
                        if (updatePager)
                        {
                            App.PropertyChangeAsync(() => ConfigurePager(userVideoList?.Page.Count ?? 0));
                        }

                        if (userVideoList == null || userVideoList.Archives.Count == 0)
                        {
                            LoadingVisibility = false;
                            NoDataVisibility = true;
                            return;
                        }

                        foreach (var video in userVideoList.Archives)
                        {
                            AddUserVideoListMedia(video);
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        return;
                    }

                    var publication = Core.BiliApi.Users.UserSpace.GetPublicationResult(
                        _mid,
                        current,
                        VideoNumberInPage,
                        tab.Id,
                        keyword: _activeSearchText);
                    if (updatePager)
                    {
                        App.PropertyChangeAsync(() => ConfigurePager(publication?.Page.Count ?? 0));
                    }

                    var publications = publication?.List;
                    if (publications == null)
                    {
                        // 没有数据，UI提示
                        LoadingVisibility = false;
                        NoDataVisibility = true;
                        return;
                    }

                    var videos = publications.Vlist;
                    if (videos == null)
                    {
                        // 没有数据，UI提示
                        LoadingVisibility = false;
                        NoDataVisibility = true;
                        return;
                    }

                    foreach (var video in videos)
                    {
                        // 查询、保存封面
                        var coverUrl = video.Pic;

                        // 播放数
                        var play = string.Empty;
                        if (video.Play > 0)
                        {
                            play = Format.FormatNumber(video.Play);
                        }
                        else
                        {
                            play = "--";
                        }

                        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
                        var dateCTime = startTime.AddSeconds(video.Created);
                        var ctime = dateCTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
                        App.PropertyChangeAsync(() =>
                        {
                            var media = new PublicationMedia(EventAggregator)
                            {
                                Avid = video.Aid,
                                Bvid = video.Bvid,
                                Cover = _defaultPic,
                                Duration = video.Length,
                                Title = video.Title,
                                PlayNumber = play,
                                CreateTime = ctime,
                                CoverUrl = coverUrl
                            };
                            _medias.Add(media);

                            LoadingVisibility = false;
                            NoDataVisibility = false;
                        });

                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private async Task SearchUserVideoListAsync()
        {
            var cancellationToken = ReplaceCancellationSource(ref _tokenSource);
            try
            {
                IsEnabled = false;
                if (_allUserVideoListArchives == null)
                {
                    _allUserVideoListArchives = await Task.Run(
                        () => GetAllUserVideoListArchives(_mid, cancellationToken),
                        cancellationToken).ConfigureAwait(true);
                }

                _userVideoSearchResults = FilterUserVideoList(
                    _allUserVideoListArchives,
                    _activeSearchText);
                ConfigurePager(_userVideoSearchResults.Count);

                IsEnabled = true;
                await UpdatePublication(1).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private static IReadOnlyList<UserVideoListArchive> GetAllUserVideoListArchives(
            long mid,
            CancellationToken cancellationToken)
        {
            const int pageSize = 50;
            var first = Core.BiliApi.Users.UserSpace.GetUserVideoList(mid, 1, pageSize, cancellationToken);
            if (first == null || first.Archives.Count == 0)
            {
                return Array.Empty<UserVideoListArchive>();
            }

            var result = new List<UserVideoListArchive>(first.Archives);
            var pageCount = (int)Math.Ceiling((double)first.Page.Count / pageSize);
            for (var page = 2; page <= pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = Core.BiliApi.Users.UserSpace.GetUserVideoList(
                    mid,
                    page,
                    pageSize,
                    cancellationToken);
                if (data == null || data.Archives.Count == 0)
                {
                    break;
                }

                result.AddRange(data.Archives);
            }

            return result;
        }

        internal static IReadOnlyList<UserVideoListArchive> FilterUserVideoList(
            IEnumerable<UserVideoListArchive> videos,
            string keyword)
        {
            ArgumentNullException.ThrowIfNull(videos);
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return videos.ToList();
            }

            var normalized = keyword.Trim();
            return videos
                .Where(video => video.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        internal static bool ShouldRestoreListState(
            bool preserveStateOnReturn,
            long currentMid,
            bool currentIsUserVideoList,
            long incomingMid,
            bool incomingIsUserVideoList,
            int loadedTabCount)
        {
            return preserveStateOnReturn
                && loadedTabCount > 0
                && currentMid == incomingMid
                && currentIsUserVideoList == incomingIsUserVideoList;
        }

        private void ConfigurePager(int itemCount)
        {
            Pager = new CustomPagerViewModel(
                1,
                Math.Max(1, (int)Math.Ceiling((double)itemCount / VideoNumberInPage)));
            Pager.CurrentChanging += OnCurrentChangedPager;
            Pager.CountChanged += OnCountChangedPager;
        }

        private void AddUserVideoListMedia(UserVideoListArchive video)
        {
            var play = video.Stat.View > 0 ? Format.FormatNumber(video.Stat.View) : "--";
            var createTime = DateTimeOffset.FromUnixTimeSeconds(video.Pubdate)
                .ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

            App.PropertyChangeAsync(() =>
            {
                Medias.Add(new PublicationMedia(EventAggregator)
                {
                    Avid = video.Aid,
                    Bvid = video.Bvid,
                    Cover = _defaultPic,
                    Duration = Format.FormatDuration2(video.Duration),
                    Title = video.Title,
                    PlayNumber = play,
                    CreateTime = createTime,
                    CoverUrl = video.Pic
                });

                LoadingVisibility = false;
                NoDataVisibility = false;
            });
        }

        private async Task InitializeUserVideoListAsync()
        {
            var cancellationToken = ReplaceCancellationSource(ref _tokenSource);
            try
            {
                LoadingVisibility = true;
                IsEnabled = false;
                var list = await Task.Run(
                    () => Core.BiliApi.Users.UserSpace.GetUserVideoList(_mid, 1, 1, cancellationToken),
                    cancellationToken).ConfigureAwait(true);
                if (list == null || list.Page.Count <= 0)
                {
                    LoadingVisibility = false;
                    NoDataVisibility = true;
                    return;
                }

                TabHeaders.Add(new TabHeader
                {
                    Id = 0,
                    Title = DictionaryResource.GetString("AllPublicationZones"),
                    SubTitle = list.Page.Count.ToString(CultureInfo.CurrentCulture)
                });
                SelectTabId = 0;

                Pager = new CustomPagerViewModel(
                    1,
                    (int)Math.Ceiling((double)list.Page.Count / VideoNumberInPage));
                Pager.CurrentChanging += OnCurrentChangedPager;
                Pager.CountChanged += OnCountChangedPager;

                IsEnabled = true;
                await UpdatePublication(1).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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

            SearchIcon = ButtonIcon.Instance().GeneralSearch;
            SearchIcon.Fill = DictionaryResource.GetColor("ColorPrimary");

            TabHeaders.Clear();
            Medias.Clear();
            SelectTabId = -1;
            IsSelectAll = false;
            InputSearchText = string.Empty;
            _activeSearchText = string.Empty;
            _allUserVideoListArchives = null;
            _userVideoSearchResults = null;
        }

        /// <summary>
        /// 导航到页面时执行
        /// </summary>
        /// <param name="navigationContext"></param>
        public override void OnNavigatedFrom(NavigationContext navigationContext)
        {
            ArgumentNullException.ThrowIfNull(navigationContext);
            base.OnNavigatedFrom(navigationContext);

            _preserveStateOnReturn = !_resetStateOnNavigationAway;
            _resetStateOnNavigationAway = false;
        }

        public override void OnNavigatedTo(NavigationContext navigationContext)
        {
            ArgumentNullException.ThrowIfNull(navigationContext);
            base.OnNavigatedTo(navigationContext);

            // 根据传入参数不同执行不同任务
            var parameter = navigationContext.Parameters.GetValue<Dictionary<string, object>>("Parameter");
            if (parameter == null)
            {
                _preserveStateOnReturn = false;
                return;
            }

            var incomingMid = (long)parameter["mid"];
            var incomingIsUserVideoList = parameter.TryGetValue("userVideoList", out var source)
                && source is true;
            if (ShouldRestoreListState(
                    _preserveStateOnReturn,
                    _mid,
                    _isUserVideoList,
                    incomingMid,
                    incomingIsUserVideoList,
                    TabHeaders.Count))
            {
                _preserveStateOnReturn = false;
                return;
            }

            _preserveStateOnReturn = false;

            InitView();

            _mid = incomingMid;
            _isUserVideoList = incomingIsUserVideoList;
            if (_isUserVideoList)
            {
                PageTitle = DictionaryResource.GetString("UserVideoList");
                RunFireAndForget(InitializeUserVideoListAsync(), nameof(InitializeUserVideoListAsync));
                return;
            }

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
            Pager.CurrentChanging += OnCurrentChangedPager;
            Pager.CountChanged += OnCountChangedPager;
            Pager.Current = 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                _tokenSource?.Cancel();
                _tokenSource?.Dispose();
                _tokenSource = null;
                _defaultPic.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
