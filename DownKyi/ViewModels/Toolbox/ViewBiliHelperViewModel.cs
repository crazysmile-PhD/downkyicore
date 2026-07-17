using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.Services.Toolbox;
using Microsoft.Extensions.Logging;
using Prism.Commands;

namespace DownKyi.ViewModels.Toolbox;

internal class ViewBiliHelperViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxBiliHelper";
    private readonly IBiliHelperCoordinator _coordinator;
    private readonly ILogger<ViewBiliHelperViewModel> _logger;
    private readonly IPlatformLauncher _platformLauncher;
    private CancellationTokenSource? _lookupCancellation;

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

    public ViewBiliHelperViewModel(
        IDesktopInteractionContext desktopInteractions,
        IBiliHelperCoordinator coordinator,
        IPlatformLauncher platformLauncher,
        ILogger<ViewBiliHelperViewModel> logger) : base(desktopInteractions)
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
            Notifications.Show("无法打开视频页面");
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
        var cancellationToken = ReplaceCancellationSource(ref _lookupCancellation);
        try
        {
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
        }
        catch (Exception e) when (e is ArgumentException or FormatException
            or InvalidOperationException or OverflowException)
        {
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
            Notifications.Show("无法打开用户空间");
        }
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _lookupCancellation);
        }

        base.Dispose(disposing);
    }
}
