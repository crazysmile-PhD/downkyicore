using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Users.Models;

namespace DownKyi.ViewModels.UserSpace;

internal class ViewChannelViewModel : ViewModelBase
{
    public const string Tag = "PageUserSpaceChannel";

    private long mid = -1;

    #region 页面属性申明

    private ObservableCollection<Channel> channels = new();

    public ObservableCollection<Channel> Channels
    {
        get => channels;
        private set => SetProperty(ref channels, value);
    }

    private int selectedItem;

    public int SelectedItem
    {
        get => selectedItem;
        set => SetProperty(ref selectedItem, value);
    }

    #endregion

    public ViewChannelViewModel(IDesktopInteractionContext desktopInteractions)
        : base(desktopInteractions)
    {
        #region 属性初始化

        Channels = new ObservableCollection<Channel>();

        #endregion
    }

    #region 命令申明

    // 视频选择事件
    private RelayCommand<object>? channelsCommand;

    public RelayCommand<object> ChannelsCommand =>
        channelsCommand ?? (channelsCommand = RequiredParameterCommand.Create<object>(ExecuteChannelsCommand));

    /// <summary>
    /// 视频选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteChannelsCommand(object parameter)
    {
        if (!(parameter is Channel channel))
        {
            return;
        }

        Dictionary<string, object> data = new Dictionary<string, object>
        {
            { "mid", mid },
            { "cid", channel.Cid },
            { "name", channel.Name },
            { "count", channel.Count }
        };

        // 进入视频页面

        SelectedItem = -1;
    }

    #endregion

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        base.OnNavigatedFrom(navigationContext);

        Channels.Clear();
        SelectedItem = -1;
    }

    /// <summary>
    /// 接收mid参数
    /// </summary>
    /// <param name="navigationContext"></param>
    public async override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        Channels.Clear();
        SelectedItem = -1;

        // 根据传入参数不同执行不同任务
        var parameter = navigationContext.Parameters.GetValue<List<SpaceChannelList>>("object");
        if (parameter == null)
        {
            return;
        }

        // 传入mid
        mid = navigationContext.Parameters.GetValue<long>("mid");

        foreach (var channel in parameter)
        {
            if (channel.Count <= 0)
            {
                continue;
            }

            // 当地时区
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); ;
            var dateCTime = startTime.AddSeconds(channel.Mtime);
            var mtime = dateCTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

            Channels.Add(new Channel
            {
                Cid = channel.Cid,
                // Cover = image,
                Name = channel.Name,
                Count = channel.Count,
                Ctime = mtime
            });
        }
    }
}
