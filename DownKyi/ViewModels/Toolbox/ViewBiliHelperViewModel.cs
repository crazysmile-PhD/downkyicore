using System;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Logging;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Events;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.ViewModels.Toolbox;

public class ViewBiliHelperViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxBiliHelper";

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

    public ViewBiliHelperViewModel(IEventAggregator eventAggregator) : base(eventAggregator)
    {
        #region 属性初始化

        #endregion
    }

    #region 命令申明

    // 输入avid事件
    private DownKyiAsyncDelegateCommand<string>? _avidCommand;

    public DownKyiAsyncDelegateCommand<string> AvidCommand => _avidCommand ??= new DownKyiAsyncDelegateCommand<string>(ExecuteAvidCommand);

    /// <summary>
    /// 输入avid事件
    /// </summary>
    private async Task ExecuteAvidCommand(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            return;
        }

        if (!ParseEntrance.IsAvId(parameter))
        {
            return;
        }

        var avid = ParseEntrance.GetAvId(parameter);
        if (avid == -1)
        {
            return;
        }

        await Task.Run(() => { Bvid = BvId.Av2Bv(avid); });
    }

    // 输入bvid事件
    private DownKyiAsyncDelegateCommand<string>? _bvidCommand;

    public DownKyiAsyncDelegateCommand<string> BvidCommand => _bvidCommand ??= new DownKyiAsyncDelegateCommand<string>(ExecuteBvidCommand);

    /// <summary>
    /// 输入bvid事件
    /// </summary>
    /// <param name="parameter"></param>
    private async Task ExecuteBvidCommand(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            return;
        }

        if (!ParseEntrance.IsBvId(parameter))
        {
            return;
        }

        await Task.Run(() =>
        {
            var avid = BvId.Bv2Av(parameter);
            Avid = $"av{avid}";
        });
    }

    // 访问网页事件
    private DownKyiAsyncDelegateCommand? _gotoWebCommand;

    public DownKyiAsyncDelegateCommand GotoWebCommand => _gotoWebCommand ??= new DownKyiAsyncDelegateCommand(ExecuteGotoWebCommand);

    /// <summary>
    /// 访问网页事件
    /// </summary>
    private async Task ExecuteGotoWebCommand()
    {
        var url = $"https://www.bilibili.com/video/{Bvid}";
        await PlatformHelper.OpenUrl(url, EventAggregator);
    }

    // 查询弹幕发送者事件
    private DownKyiAsyncDelegateCommand? _findDanmakuSenderCommand;

    public DownKyiAsyncDelegateCommand FindDanmakuSenderCommand => _findDanmakuSenderCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFindDanmakuSenderCommand);

    /// <summary>
    /// 查询弹幕发送者事件
    /// </summary>
    private async Task ExecuteFindDanmakuSenderCommand()
    {
        await Task.Run(() =>
        {
            try
            {
                UserMid = DanmakuSender.FindDanmakuSender(DanmakuUserId);
            }
            catch (Exception e)
            {
                UserMid = null;

                Console.PrintLine("FindDanmakuSenderCommand()发生异常: {0}", e);
                LogManager.Error(Tag, e);
            }
        });
    }

    // 访问用户空间事件
    private DownKyiAsyncDelegateCommand? _visitUserSpaceCommand;

    public DownKyiAsyncDelegateCommand VisitUserSpaceCommand => _visitUserSpaceCommand ??= new DownKyiAsyncDelegateCommand(ExecuteVisitUserSpaceCommand);

    /// <summary>
    /// 访问用户空间事件
    /// </summary>
    private async Task ExecuteVisitUserSpaceCommand()
    {
        if (UserMid == null)
        {
            return;
        }

        var userSpace = $"https://space.bilibili.com/{UserMid}";
        await PlatformHelper.OpenUrl(userSpace, EventAggregator);
    }

    #endregion
}
