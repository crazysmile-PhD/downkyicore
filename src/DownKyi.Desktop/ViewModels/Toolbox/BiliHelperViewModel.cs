using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Services.Toolbox;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Toolbox;

internal class BiliHelperViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxBiliHelper";
    private readonly IBiliHelperCoordinator _coordinator;
    private readonly ILogger<BiliHelperViewModel> _logger;
    private readonly IPlatformLauncher _platformLauncher;
    private CancellationTokenSource? _lookupCancellation;
    private CancellationToken _pageLifetime;

    #region 页面属性申明

    private string _avid = string.Empty;

    public string Avid
    {
        get => _avid;
        set => SetProperty(ref _avid, value);
    }

    private string _bvid = string.Empty;

    public string Bvid
    {
        get => _bvid;
        set => SetProperty(ref _bvid, value);
    }

    private string _danmakuUserId = string.Empty;

    public string DanmakuUserId
    {
        get => _danmakuUserId;
        set => SetProperty(ref _danmakuUserId, value);
    }

    private string? _userMid;

    public string? UserMid
    {
        get => _userMid;
        set => SetProperty(ref _userMid, value);
    }

    #endregion

    public BiliHelperViewModel(
        IDesktopInteractionContext desktopInteractions,
        IBiliHelperCoordinator coordinator,
        IPlatformLauncher platformLauncher,
        ILogger<BiliHelperViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        #endregion
    }

    #region 命令申明

    // 输入avid事件
    private DownKyiAsyncDelegateCommand<string>? _avidCommand;

    public DownKyiAsyncDelegateCommand<string> AvidCommand => _avidCommand ??= new DownKyiAsyncDelegateCommand<string>(ExecuteAvidCommand, _logger);

    /// <summary>
    /// 输入avid事件
    /// </summary>
    private Task ExecuteAvidCommand(string? parameter)
    {
        var bvid = _coordinator.ConvertAvidToBvid(parameter);
        if (bvid != null)
        {
            Bvid = bvid;
        }

        return Task.CompletedTask;
    }

    // 输入bvid事件
    private DownKyiAsyncDelegateCommand<string>? _bvidCommand;

    public DownKyiAsyncDelegateCommand<string> BvidCommand => _bvidCommand ??= new DownKyiAsyncDelegateCommand<string>(ExecuteBvidCommand, _logger);

    /// <summary>
    /// 输入bvid事件
    /// </summary>
    /// <param name="parameter"></param>
    private Task ExecuteBvidCommand(string? parameter)
    {
        var avid = _coordinator.ConvertBvidToAvid(parameter);
        if (avid != null)
        {
            Avid = avid;
        }

        return Task.CompletedTask;
    }

    // 访问网页事件
    private DownKyiAsyncDelegateCommand? _gotoWebCommand;

    public DownKyiAsyncDelegateCommand GotoWebCommand => _gotoWebCommand ??= new DownKyiAsyncDelegateCommand(ExecuteGotoWebCommand, _logger);

    /// <summary>
    /// 访问网页事件
    /// </summary>
    private async Task ExecuteGotoWebCommand()
    {
        var uri = new Uri($"https://www.bilibili.com/video/{Bvid}");
        if (!await _platformLauncher.OpenUriAsync(uri).ConfigureAwait(true))
        {
            Notifications.Show(DictionaryResource.GetString("OpenVideoPageFailed"));
        }
    }

    // 查询弹幕发送者事件
    private DownKyiAsyncDelegateCommand? _findDanmakuSenderCommand;

    public DownKyiAsyncDelegateCommand FindDanmakuSenderCommand => _findDanmakuSenderCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFindDanmakuSenderCommand, _logger);

    /// <summary>
    /// 查询弹幕发送者事件
    /// </summary>
    private async Task ExecuteFindDanmakuSenderCommand()
    {
        var cancellationToken = ReplaceCancellationSource(ref _lookupCancellation, _pageLifetime);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userMid = await _coordinator
                .FindDanmakuSenderAsync(DanmakuUserId, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_lookupCancellation?.Token == cancellationToken)
            {
                UserMid = userMid;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is ArgumentException or FormatException
            or InvalidOperationException or OverflowException)
        {
            if (cancellationToken.IsCancellationRequested) return;
            UserMid = null;
            _logger.LogErrorMessage($"Danmaku sender lookup failed ({e.GetType().Name}).");
        }
    }

    // 访问用户空间事件
    private DownKyiAsyncDelegateCommand? _visitUserSpaceCommand;

    public DownKyiAsyncDelegateCommand VisitUserSpaceCommand => _visitUserSpaceCommand ??= new DownKyiAsyncDelegateCommand(ExecuteVisitUserSpaceCommand, _logger);

    /// <summary>
    /// 访问用户空间事件
    /// </summary>
    private async Task ExecuteVisitUserSpaceCommand()
    {
        if (UserMid == null)
        {
            return;
        }

        var userSpace = new Uri($"https://space.bilibili.com/{UserMid}");
        if (!await _platformLauncher.OpenUriAsync(userSpace).ConfigureAwait(true))
        {
            Notifications.Show(DictionaryResource.GetString("OpenUserSpaceFailed"));
        }
    }

    #endregion

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);
        _pageLifetime = navigationContext.Parameters.GetValue<CancellationToken>("lifetime");
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelAndDispose(ref _lookupCancellation);
        base.OnNavigatedFrom(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _lookupCancellation);
        }

        base.Dispose(disposing);
    }
}
