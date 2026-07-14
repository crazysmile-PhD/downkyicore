using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.UiState;
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
    private readonly VideoSearchState _videoSearchState = new();
    private readonly IClipboardService _clipboardService;
    private CancellationTokenSource? _operationCancellation;
    private int _operationVersion;

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

    public VideoDetailUiState UiState { get; } = new();

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

    private bool _isSelectAll;

    public bool IsSelectAll
    {
        get => _isSelectAll;
        set => SetProperty(ref _isSelectAll, value);
    }

    private int _gridResetVersion;

    public int GridResetVersion
    {
        get => _gridResetVersion;
        private set => SetProperty(ref _gridResetVersion, value);
    }

    #endregion

    public ViewVideoDetailViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IClipboardService clipboardService) : base(eventAggregator, dialogService)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        // 下载管理按钮
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        VideoSections = new RangeObservableCollection<VideoSection>();
    }

    private CancellationToken ResetOperationCancellation(out int operationVersion)
    {
        operationVersion = Interlocked.Increment(ref _operationVersion);
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _operationCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();
        return replacement.Token;
    }

    private void CancelOperation()
    {
        Interlocked.Increment(ref _operationVersion);
        _operationCancellation?.Cancel();
    }

    private bool IsCurrentOperation(int operationVersion)
    {
        return operationVersion == Volatile.Read(ref _operationVersion);
    }

    private void SetDisplayState(VideoDetailDisplayState state)
    {
        if (UiDispatcher.CheckAccess())
        {
            UiState.DisplayState = state;
            return;
        }

        UiDispatcher.Post(() => UiState.DisplayState = state);
    }

    private void RestoreDisplayState()
    {
        SetDisplayState(VideoInfoView == null
            ? VideoDetailDisplayState.Idle
            : VideoDetailDisplayState.Content);
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
        if (TryNavigateBack())
        {
            return;
        }

        var parameter = new NavigationParam
        {
            ViewName = string.IsNullOrWhiteSpace(ParentView) ? ViewIndexViewModel.Tag : ParentView,
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
    private Task ExecuteInputSearchCommandAsync()
    {
        _videoSearchState.Apply(InputSearchText);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理输入事件
    /// </summary>
    private Task ExecuteInputCommandAsync()
    {
        return ExecuteInputCommandAsync(InputText);
    }

    private async Task ExecuteInputCommandAsync(string? requestedInput)
    {
        var cancellationToken = ResetOperationCancellation(out var operationVersion);
        InitView();
        try
        {
            if (string.IsNullOrWhiteSpace(requestedInput))
            {
                SetDisplayState(VideoDetailDisplayState.Idle);
                return;
            }

            var input = Regex.Replace(requestedInput, @"[【]*[^【]*[^】]*[】 ]", "");
            InputText = input;
            _input = input;
            LogManager.Debug(Tag, "Processing captured video input.");
            var parseResult = await _parseCoordinator.LoadDetailAsync(
                input,
                refresh: true,
                cancellationToken).ConfigureAwait(true);
            await UiDispatcher.InvokeAsync(() => ApplyVideoDetailResult(parseResult, cancellationToken));

            if (!IsCurrentOperation(operationVersion))
            {
                return;
            }

            if (SettingsManager.Instance.GetIsAutoParseVideo() == AllowStatus.Yes)
            {
                RunFireAndForget(ExecuteParseAllVideoCommandAsync(), nameof(ExecuteParseAllVideoCommandAsync));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(operationVersion))
            {
                SetDisplayState(VideoDetailDisplayState.Idle);
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            if (!IsCurrentOperation(operationVersion))
            {
                return;
            }

            Console.PrintLine("InputCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            SetDisplayState(VideoDetailDisplayState.Empty);
        }
    }

    /// <summary>
    /// 输入事件是否允许执行
    /// </summary>
    /// <returns></returns>
    private bool CanExecuteInputCommand()
    {
        return !UiState.IsBusy;
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
        await _clipboardService.SetTextAsync(_videoInfoView.CoverUrl).ConfigureAwait(true);
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

    // 全选事件
    private DelegateCommand? _selectAllCommand;
    public DelegateCommand SelectAllCommand => _selectAllCommand ??= new DelegateCommand(ExecuteSelectAllCommand);

    /// <summary>
    /// 全选事件
    /// </summary>
    private void ExecuteSelectAllCommand()
    {
        var section = VideoSelectionState.GetSelectedSection(VideoSections);
        VideoSelectionState.SetAllSelected(section, IsSelectAll);
    }

    private DelegateCommand? _clearSelectionCommand;

    public DelegateCommand ClearSelectionCommand =>
        _clearSelectionCommand ??= new DelegateCommand(ExecuteClearSelectionCommand);

    private void ExecuteClearSelectionCommand()
    {
        VideoSelectionState.SetAllSelected(
            VideoSelectionState.GetSelectedSection(VideoSections),
            isSelected: false);
        IsSelectAll = false;
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

        var cancellationToken = ResetOperationCancellation(out var operationVersion);
        SetDisplayState(VideoDetailDisplayState.Busy);

        try
        {
            LogManager.Debug(Tag, $"Video Page: {videoPage.Cid}");
            var parseResult = await _parseCoordinator.LoadPageStreamAsync(
                _input,
                videoPage,
                refresh: true,
                cancellationToken).ConfigureAwait(true);
            if (parseResult != null)
            {
                await UiDispatcher.InvokeAsync(() => ApplyVideoStreamResult(parseResult, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(operationVersion))
            {
                RestoreDisplayState();
            }

            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            if (!IsCurrentOperation(operationVersion))
            {
                return;
            }

            Console.PrintLine("ParseCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            RestoreDisplayState();
            return;
        }

        if (IsCurrentOperation(operationVersion))
        {
            RestoreDisplayState();
        }
    }

    /// <summary>
    /// 解析视频流事件是否允许执行
    /// </summary>
    /// <param name="parameter"></param>
    /// <returns></returns>
    private bool CanExecuteParseCommand(object parameter)
    {
        return !UiState.IsBusy;
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
        return !UiState.IsBusy;
    }

    private async Task ExecuteParse(ParseScope parseScope)
    {
        var cancellationToken = ResetOperationCancellation(out var operationVersion);
        try
        {
            SetDisplayState(VideoDetailDisplayState.Busy);
            LogManager.Debug(Tag, "Parse video");
            var pages = VideoSelectionState.GetPagesForScope(VideoSections, parseScope);
            var parseResults = await _parseCoordinator.LoadPageStreamsAsync(
                _input,
                pages,
                cancellationToken).ConfigureAwait(true);
            await UiDispatcher.InvokeAsync(() => ApplyVideoStreamResults(parseResults, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(operationVersion))
            {
                RestoreDisplayState();
            }

            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or RegexMatchTimeoutException or Newtonsoft.Json.JsonException)
        {
            if (!IsCurrentOperation(operationVersion))
            {
                return;
            }

            Console.PrintLine("ParseCommand()发生异常: {0}", e);
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);

            RestoreDisplayState();
            return;
        }

        if (!IsCurrentOperation(operationVersion))
        {
            return;
        }

        RestoreDisplayState();

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
        return !UiState.IsBusy;
    }

    #endregion

    #region 业务逻辑

    /// <summary>
    /// 初始化页面元素
    /// </summary>
    private void InitView()
    {
        LogManager.Debug(Tag, "初始化页面元素");
        GridResetVersion++;
        SetDisplayState(VideoDetailDisplayState.Busy);
        VideoSections.ReplaceRange(Array.Empty<VideoSection>());
        _videoSearchState.Clear();
        _parseCoordinator.Reset();
    }


    private void ApplyVideoDetailResult(
        VideoDetailParseResult parseResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        cancellationToken.ThrowIfCancellationRequested();

        VideoInfoView = parseResult.VideoInfoView;
        if (VideoInfoView == null)
        {
            LogManager.Debug(Tag, "Video detail is unavailable.");
            SetDisplayState(VideoDetailDisplayState.Empty);
            return;
        }

        _videoSearchState.Reset(parseResult.VideoSections);
        VideoSections.ReplaceRange(parseResult.VideoSections);
        AutoLocateAndSelectVideoPosition();
        SetDisplayState(VideoDetailDisplayState.Content);
    }

    private static void ApplyVideoStreamResult(
        VideoStreamParseResult parseResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Services.Utils.VideoPageInfo(parseResult.PlayUrl, parseResult.Page);
    }

    private static void ApplyVideoStreamResults(
        IReadOnlyList<VideoStreamParseResult> parseResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var parseResult in parseResults)
        {
            Services.Utils.VideoPageInfo(parseResult.PlayUrl, parseResult.Page);
        }
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

        // 遍历所有视频页面，找到对应的位置
        foreach (var section in VideoSections)
        {
            section.IsSelected = true;
            foreach (var page in section.VideoPages)
            {
                if (page.Avid != avid && page.Bvid != bvid)
                {
                    continue;
                }

                page.IsSelected = true;
                // Trigger the selected-item binding and scroll behavior.
                SelectedVideoPage = page;
                return;
            }
        }
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
            async directory =>
            {
                // 传递video对象
                addToDownloadService.GetVideo(videoInfoView, videoSections);
                // 下载
                return await addToDownloadService.AddToDownload(EventAggregator, DialogService, directory, isAll).ConfigureAwait(true);
            }).ConfigureAwait(true);

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
            var input = param.Replace(AppConstant.ClipboardId, "", StringComparison.Ordinal);

            // 检测是否从剪贴板传入
            if (InputText == input && param.EndsWith(AppConstant.ClipboardId, StringComparison.Ordinal))
            {
                return;
            }

            // 正在执行任务时不开启新任务
            if (!UiState.IsBusy)
            {
                InputText = input;
                RunFireAndForget(ExecuteInputCommandAsync(input), nameof(ExecuteInputCommandAsync));
            }
        }

        base.OnNavigatedTo(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            Interlocked.Increment(ref _operationVersion);
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }

        base.Dispose(disposing);
    }
}
