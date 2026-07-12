using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.CustomAction;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.PageViewModels;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using Prism.Navigation.Regions;
using Console = DownKyi.Core.Utils.Debugging.Console;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels;

internal class ViewVideoDetailViewModel : ViewModelBase
{
    public const string Tag = "PageVideoDetail";

    // 保存输入字符串，避免被用户修改
    private string _input = string.Empty;

    private readonly VideoParseCoordinator _parseCoordinator = new();
    private CancellationTokenSource? _operationCancellation;

    #region 页面属性申明

    private string? _inputText;

    public string? InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
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

    private VectorImage _downloadManage = ButtonIcon.Instance().DownloadManage;

    public VectorImage DownloadManage
    {
        get => _downloadManage;
        set => SetProperty(ref _downloadManage, value);
    }

    private VideoInfoView? _videoInfoView;

    public VideoInfoView? VideoInfoView
    {
        get => _videoInfoView;
        set => SetProperty(ref _videoInfoView, value);
    }

    private RangeObservableCollection<VideoSection> _videoSections = new();

    public RangeObservableCollection<VideoSection> VideoSections
    {
        get => _videoSections;
        private set => SetProperty(ref _videoSections, value);
    }

    public RangeObservableCollection<VideoSection> CaCheVideoSections { get; private set; }

    private bool _isSelectAll;

    public bool IsSelectAll
    {
        get => _isSelectAll;
        set => SetProperty(ref _isSelectAll, value);
    }

    private bool _contentVisibility;

    public bool ContentVisibility
    {
        get => _contentVisibility;
        set => SetProperty(ref _contentVisibility, value);
    }

    private bool _noDataVisibility;

    public bool NoDataVisibility
    {
        get => _noDataVisibility;
        set => SetProperty(ref _noDataVisibility, value);
    }


    public ResetGridSplitterBehavior ResetGridBehavior { get; set; } = new();

    #endregion

    public ViewVideoDetailViewModel(IEventAggregator eventAggregator, IDialogService dialogService) : base(eventAggregator, dialogService)
    {
        // 初始化loading
        Loading = true;
        LoadingVisibility = false;

        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        VideoSections = new RangeObservableCollection<VideoSection>();
        CaCheVideoSections = new RangeObservableCollection<VideoSection>();
    }

    private CancellationToken ResetOperationCancellation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        return _operationCancellation.Token;
    }

    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
    }

    #region 命令申明

    // 返回
    private DelegateCommand? _backSpaceCommand;

    public DelegateCommand BackSpaceCommand => _backSpaceCommand ??= new DelegateCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        CancelOperation();
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
            ParentViewName = string.IsNullOrWhiteSpace(ParentView) ? ViewIndexViewModel.Tag : ParentView,
            Parameter = null
        };
        EventAggregator.GetEvent<NavigationEvent>().Publish(parameter);
    }

    // 输入确认事件
    private DownKyiAsyncDelegateCommand? _inputCommand;

    public ICommand InputCommand => _inputCommand ??= new DownKyiAsyncDelegateCommand(ExecuteInputCommandAsync, CanExecuteInputCommand);


    private DownKyiAsyncDelegateCommand? _inputSearchCommand;


    public ICommand InputSearchCommand => _inputSearchCommand ??= new DownKyiAsyncDelegateCommand(ExecuteInputSearchCommandAsync);

    /// <summary>
    /// 搜索视频输入事件
    /// </summary>
    private async Task ExecuteInputSearchCommandAsync()
    {
        await Task.Run(() =>
        {
            if (string.IsNullOrEmpty(InputSearchText))
            {
                foreach (var section in VideoSections)
                {
                    var cache = CaCheVideoSections.FirstOrDefault(e => e.Id == section.Id);
                    if (cache != null)
                    {
                        section.VideoPages = cache.VideoPages;
                    }
                }
            }
            else
            {
                foreach (var section in VideoSections)
                {
                    var cache = CaCheVideoSections.FirstOrDefault(e => e.Id == section.Id);

                    if (cache == null) continue;

                    var pages = cache.VideoPages.Where(e => e.Name.Contains(InputSearchText)).ToList();
                    section.VideoPages = pages;
                }
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 处理输入事件
    /// </summary>
    private async Task ExecuteInputCommandAsync()
    {
        var cancellationToken = ResetOperationCancellation();
        InitView();
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(InputText))
                {
                    return;
                }

                LogManager.Debug(Tag, $"InputText: {InputText}");
                InputText = Regex.Replace(InputText, @"[【]*[^【]*[^】]*[】 ]", "");
                _input = InputText;

                // 更新页面
                UnityUpdateView(UpdateView, _input, true, cancellationToken);

                // 是否自动解析视频
                if (SettingsManager.Instance.GetIsAutoParseVideo() == AllowStatus.Yes)
                {
                    PropertyChangeAsync(() => _ = ExecuteParseAllVideoCommandAsync());
                }
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoadingVisibility = false;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            Console.PrintLine("InputCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            LoadingVisibility = false;
            ContentVisibility = false;
            NoDataVisibility = true;
        }
    }

    /// <summary>
    /// 输入事件是否允许执行
    /// </summary>
    /// <returns></returns>
    private bool CanExecuteInputCommand()
    {
        return LoadingVisibility != true;
    }

    // 复制封面事件
    private DelegateCommand? _copyCoverCommand;

    public DelegateCommand CopyCoverCommand => _copyCoverCommand ??= new DelegateCommand(ExecuteCopyCoverCommand);

    /// <summary>
    /// 复制封面事件
    /// </summary>
    private void ExecuteCopyCoverCommand()
    {
        // 复制封面图片到剪贴板
        // Clipboard.SetImage(VideoInfoView.Cover);
        LogManager.Info(Tag, "复制封面图片到剪贴板");
    }

    // 复制封面URL事件
    private DownKyiAsyncDelegateCommand? _copyCoverUrlCommand;

    public ICommand CopyCoverUrlCommand => _copyCoverUrlCommand ??= new DownKyiAsyncDelegateCommand(ExecuteCopyCoverUrlCommandAsync);

    /// <summary>
    /// 复制封面URL事件
    /// </summary>
    private async Task ExecuteCopyCoverUrlCommandAsync()
    {
        if (_videoInfoView?.CoverUrl == null) return;
        // 复制封面url到剪贴板
        await ClipboardManager.SetText(_videoInfoView.CoverUrl).ConfigureAwait(true);
        LogManager.Info(Tag, "复制封面url到剪贴板");
    }

    // 前往UP主页事件
    private DelegateCommand? _upperCommand;
    public DelegateCommand UpperCommand => _upperCommand ??= new DelegateCommand(ExecuteUpperCommand);

    /// <summary>
    /// 前往UP主页事件
    /// </summary>
    private void ExecuteUpperCommand()
    {
        if (VideoInfoView == null)
        {
            return;
        }

        NavigateToView.NavigateToViewUserSpace(EventAggregator, Tag, VideoInfoView.UpperMid);
    }

    // 视频章节选择事件
    private DelegateCommand<object>? _videoSectionsCommand;

    public DelegateCommand<object> VideoSectionsCommand => _videoSectionsCommand ??= new DelegateCommand<object>(ExecuteVideoSectionsCommand);

    /// <summary>
    /// 视频章节选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoSectionsCommand(object parameter)
    {
        if (parameter is not DataGrid grid)
        {
            return;
        }

        var selectedSection = VideoSelectionState.GetSelectedSection(VideoSections);
        if (selectedSection?.VideoPages == null)
        {
            IsSelectAll = false;
            return;
        }

        var selectedPages = VideoSelectionState.GetSelectedPages(selectedSection);
        foreach (var page in selectedPages)
        {
            grid.SelectedItems.Add(page);
        }

        IsSelectAll = VideoSelectionState.IsAllSelected(selectedSection, selectedPages.Count);
    }

    // 视频page选择事件
    private DelegateCommand<IList>? _videoPagesCommand;

    public DelegateCommand<IList> VideoPagesCommand => _videoPagesCommand ??= new DelegateCommand<IList>(ExecuteVideoPagesCommand);

    /// <summary>
    /// 视频page选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoPagesCommand(IList parameter)
    {
        if (!(parameter is IList videoPages))
        {
            return;
        }

        var section = VideoSelectionState.GetSelectedSection(VideoSections);

        if (section == null)
        {
            return;
        }

        VideoSelectionState.ApplySelectedPages(section, parameter.Cast<VideoPage>());
        IsSelectAll = VideoSelectionState.IsAllSelected(section, videoPages.Count);
    }

    // 全选事件
    private DelegateCommand<object>? _selectAllCommand;
    public DelegateCommand<object> SelectAllCommand => _selectAllCommand ??= new DelegateCommand<object>(ExecuteSelectAllCommand);

    /// <summary>
    /// 全选事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteSelectAllCommand(object parameter)
    {
        if (parameter is not DataGrid dataGrid)
        {
            return;
        }

        if (IsSelectAll)
        {
            dataGrid.SelectAll();
        }
        else
        {
            dataGrid.SelectedIndex = -1;
        }
    }


    // 解析视频流事件
    private DownKyiAsyncDelegateCommand<object>? _parseCommand;

    public ICommand ParseCommand => _parseCommand ??= new DownKyiAsyncDelegateCommand<object>(ExecuteParseCommandAsync, CanExecuteParseCommand);

    /// <summary>
    /// 解析视频流事件
    /// </summary>
    /// <param name="parameter"></param>
    private async Task ExecuteParseCommandAsync(object? parameter)
    {
        if (parameter is not VideoPage videoPage)
        {
            return;
        }

        LoadingVisibility = true;
        var cancellationToken = ResetOperationCancellation();

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogManager.Debug(Tag, $"Video Page: {videoPage.Cid}");

                UnityUpdateView(ParseVideo, _input, videoPage, true, cancellationToken);
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoadingVisibility = false;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            Console.PrintLine("ParseCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            LoadingVisibility = false;
        }

        LoadingVisibility = false;
    }

    /// <summary>
    /// 解析视频流事件是否允许执行
    /// </summary>
    /// <param name="parameter"></param>
    /// <returns></returns>
    private bool CanExecuteParseCommand(object parameter)
    {
        return LoadingVisibility != true;
    }


    // 解析所有视频流事件
    private DownKyiAsyncDelegateCommand? _parseAllVideoCommand;

    public ICommand ParseAllVideoCommand => _parseAllVideoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteParseAllVideoCommandAsync, CanExecuteParseAllVideoCommand);

    /// <summary>
    /// 解析所有视频流事件
    /// </summary>
    private async Task ExecuteParseAllVideoCommandAsync()
    {
        // 解析范围
        var parseScope = SettingsManager.Instance.GetParseScope();

        // 是否选择了解析范围
        if (parseScope == ParseScope.None)
        {
            if (DialogService == null)
            {
                return;
            }

            //打开解析选择器
            await DialogService.ShowDialogAsync(ViewParsingSelectorViewModel.Tag, null, async result =>
            {
                if (result.Result != ButtonResult.OK) return;
                // 选择的解析范围
                parseScope = result.Parameters.GetValue<ParseScope>("parseScope");
                await ExecuteParse(parseScope).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        else
        {
            await ExecuteParse(parseScope).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 解析所有视频流事件是否允许执行
    /// </summary>
    /// <returns></returns>
    private bool CanExecuteParseAllVideoCommand()
    {
        return LoadingVisibility != true;
    }

    private async Task ExecuteParse(ParseScope parseScope)
    {
        var cancellationToken = ResetOperationCancellation();
        try
        {
            LoadingVisibility = true;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogManager.Debug(Tag, "Parse video");

                var pages = VideoSelectionState.GetPagesForScope(VideoSections, parseScope);
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 执行解析任务
                    UnityUpdateView(ParseVideo, _input, page, cancellationToken: cancellationToken);
                }
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoadingVisibility = false;
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            Console.PrintLine("ParseCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            LoadingVisibility = false;
        }

        LoadingVisibility = false;

        // 解析后是否自动下载解析视频
        var isAutoDownloadAll = SettingsManager.Instance.GetIsAutoDownloadAll();
        if (parseScope != ParseScope.None && isAutoDownloadAll == AllowStatus.Yes)
        {
            await AddToDownloadAsync(true).ConfigureAwait(true);
        }

        LogManager.Debug(Tag, $"ParseScope: {parseScope:G}");
    }

    // 添加到下载列表事件
    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public ICommand AddToDownloadCommand => _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false), CanExecuteAddToDownloadCommand);

    /// <summary>
    /// 添加到下载列表事件
    /// </summary>
    private bool CanExecuteAddToDownloadCommand()
    {
        return LoadingVisibility != true;
    }

    #endregion

    #region 业务逻辑

    /// <summary>
    /// 初始化页面元素
    /// </summary>
    private void InitView()
    {
        LogManager.Debug(Tag, "初始化页面元素");
        ResetGridBehavior.ResetGrid();
        LoadingVisibility = true;
        ContentVisibility = false;
        NoDataVisibility = false;
        VideoSections.ReplaceRange(Array.Empty<VideoSection>());
        CaCheVideoSections.ReplaceRange(Array.Empty<VideoSection>());
        _parseCoordinator.Reset();
    }


    /// <summary>
    /// 更新页面的统一方法
    /// </summary>
    /// <param name="action"></param>
    /// <param name="input"></param>
    /// <param name="page"></param>
    /// <param name="refresh"></param>
    private void UnityUpdateView(Action<IInfoService, CancellationToken> action, string input, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var infoService = GetInfoService(input, refresh, cancellationToken);
        if (infoService == null)
        {
            return;
        }

        action(infoService, cancellationToken);
    }

    private void UnityUpdateView(Action<IInfoService, VideoPage, CancellationToken> action, string input, VideoPage page, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var infoService = GetInfoService(input, refresh, cancellationToken);
        if (infoService == null)
        {
            return;
        }

        action(infoService, page, cancellationToken);
    }

    private IInfoService? GetInfoService(string input, bool refresh, CancellationToken cancellationToken)
    {
        return _parseCoordinator.GetInfoService(input, refresh, cancellationToken);
    }

    /// <summary>
    /// 更新页面
    /// </summary>
    /// <param name="videoInfoService"></param>
    /// <param name="param"></param>
    private void UpdateView(IInfoService videoInfoService, CancellationToken cancellationToken)
    {
        // 获取视频详情
        VideoInfoView = videoInfoService.GetVideoView(cancellationToken);
        if (VideoInfoView == null)
        {
            LogManager.Debug(Tag, "VideoInfoView is null.");

            LoadingVisibility = false;
            ContentVisibility = false;
            NoDataVisibility = true;
            return;
        }
        else
        {
            LoadingVisibility = false;
            ContentVisibility = true;
            NoDataVisibility = false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 获取视频列表
        var videoSections = videoInfoService.GetVideoSections(false, cancellationToken);

        // 添加新数据
        if (videoSections == null)
        {
            LogManager.Debug(Tag, "videoSections is not exist.");

            var pages = videoInfoService.GetVideoPages(cancellationToken) ?? new List<VideoPage>();
            var cachePages = pages.Select(page => page.CloneForCache()).ToList();
            var defaultSections = new[]
            {
                new VideoSection
                {
                    Id = 0,
                    Title = "default",
                    IsSelected = true,
                    VideoPages = pages
                }
            };
            var defaultCacheSections = new[]
            {
                new VideoSection
                {
                    Id = 0,
                    Title = "default",
                    IsSelected = true,
                    VideoPages = cachePages
                }
            };

            PropertyChangeAsync(() =>
            {
                VideoSections.ReplaceRange(defaultSections);
                CaCheVideoSections.ReplaceRange(defaultCacheSections);

                // 自动定位到合集中对应的视频位置
                AutoLocateAndSelectVideoPosition();
            });
        }
        else
        {
            var videoSectionsData = videoSections.Select(section => section.CloneForCache()).ToList();
            PropertyChangeAsync(() =>
            {
                VideoSections.ReplaceRange(videoSections);
                CaCheVideoSections.ReplaceRange(videoSectionsData);

                // 自动定位到合集中对应的视频位置
                AutoLocateAndSelectVideoPosition();
            });
        }
    }

    /// <summary>
    /// 解析视频流
    /// </summary>
    /// <param name="videoInfoService"></param>
    /// <param name="videoPage"></param>
    private void ParseVideo(IInfoService videoInfoService, VideoPage videoPage, CancellationToken cancellationToken)
    {
        videoInfoService.GetVideoStream(videoPage, cancellationToken);
    }

    /// <summary>
    /// 自动定位到合集中对应的视频位置
    /// </summary>
    private VideoPage? selectedVideoPage;
    public VideoPage? SelectedVideoPage
    {
        get => selectedVideoPage;
        set => SetProperty(ref selectedVideoPage, value);
    }

    private void AutoLocateAndSelectVideoPosition()
    {

        long avid = ParseEntrance.GetAvId(_input);
        string bvid = ParseEntrance.GetBvId(_input);

        // 确保在UI线程上更新属性
        App.PropertyChangeAsync(() =>
        {
            // 遍历所有视频页面，找到对应的位置
            foreach (var section in VideoSections)
            {
                section.IsSelected = true;
                foreach (var page in section.VideoPages)
                {

                    if (page.Avid == avid || page.Bvid == bvid)
                    {
                        // 选中对应的视频页面
                        page.IsSelected = true;
                        // 设置SelectedVideoPage以触发DataGrid的SelectedItem变化和滚动操作
                        SelectedVideoPage = page;
                        return;
                    }
                }
            }
        });
    }

    /// <summary>
    /// 添加到下载列表事件
    /// </summary>
    /// <param name="isAll">是否下载所有，包括未选中项</param>
    private async Task AddToDownloadAsync(bool isAll)
    {
        var playStreamType = VideoInputResolver.ResolvePlayStreamType(_input);
        if (playStreamType == null)
        {
            return;
        }

        var addToDownloadService = new AddToDownloadService(playStreamType.Value);
        var videoInfoView = VideoInfoView;
        if (videoInfoView == null)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipAddDownloadingZero"));
            return;
        }

        var videoSections = VideoSections.ToList();

        // 视频计数
        var addedCount = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => addToDownloadService.SetDirectory(DialogService),
            directory => Task.Run(async () =>
            {
                // 传递video对象
                addToDownloadService.GetVideo(videoInfoView, videoSections);
                // 下载
                return await addToDownloadService.AddToDownload(EventAggregator, DialogService, directory, isAll).ConfigureAwait(true);
            })).ConfigureAwait(true);

        if (addedCount == null)
        {
            return;
        }

        var i = addedCount.Value;

        // 通知用户添加到下载列表的结果
        if (i <= 0)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipAddDownloadingZero"));
        }
        else
        {
            EventAggregator.GetEvent<MessageEvent>()
                .Publish(
                    $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{i}{DictionaryResource.GetString("TipAddDownloadingFinished2")}");
        }
    }

    #endregion

    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");
        // Parent参数为null时，表示是从下一个页面返回到本页面，不需要执行任务
        if (navigationContext.Parameters.GetValue<string>("Parent") != null)
        {
            var param = navigationContext.Parameters.GetValue<string>("Parameter");
            // 移除剪贴板id
            var input = param.Replace(AppConstant.ClipboardId, "");

            // 检测是否从剪贴板传入
            if (InputText == input && param.EndsWith(AppConstant.ClipboardId))
            {
                return;
            }

            // 正在执行任务时不开启新任务
            if (LoadingVisibility != true)
            {
                InputText = input;
                PropertyChangeAsync(() => _ = ExecuteInputCommandAsync());
            }
        }

        base.OnNavigatedTo(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }

        base.Dispose(disposing);
    }
}
