using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;

namespace DownKyi.Presentation;

internal class FriendInfo : ObservableObject
{
    private readonly IAppNavigationService _navigationService;
    private readonly AppRoute _parentRoute;

    public FriendInfo(IAppNavigationService navigationService, AppRoute parentRoute)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _parentRoute = parentRoute;
    }

    public long Mid { get; set; }

    #region 页面属性申明

    private string _header = string.Empty;

    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _sign = string.Empty;

    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }

    #endregion

    #region 命令申明

    // 视频标题点击事件
    private RelayCommand? _userCommand;

    public RelayCommand UserCommand => _userCommand ??= new RelayCommand(ExecuteUserCommand);

    /// <summary>
    /// 视频标题点击事件
    /// </summary>
    private void ExecuteUserCommand()
    {
        _navigationService.Navigate(new AppNavigationRequest(AppRoute.UserSpace, _parentRoute, Mid));
    }

    #endregion
}
