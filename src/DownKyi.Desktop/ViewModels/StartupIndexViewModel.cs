using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Images;

namespace DownKyi.ViewModels;

internal sealed class StartupIndexViewModel : ObservableObject, IIndexSurfaceViewModel
{
    private readonly Action<AppNavigationRequest> _queueNavigation;
    private readonly Action<string> _queueSearch;
    private string _inputText = string.Empty;

    public StartupIndexViewModel(
        UserApplicationSettings user,
        Action<AppNavigationRequest> queueNavigation,
        Action<string> queueSearch)
    {
        ArgumentNullException.ThrowIfNull(user);
        _queueNavigation = queueNavigation ?? throw new ArgumentNullException(nameof(queueNavigation));
        _queueSearch = queueSearch ?? throw new ArgumentNullException(nameof(queueSearch));

        UserName = user.IsLogin ? user.Name : null;
        LoginCommand = new RelayCommand(() => _queueNavigation(
            user.IsLogin && user.Mid >= 0
                ? new AppNavigationRequest(AppRoute.MySpace, AppRoute.Index, user.Mid)
                : new AppNavigationRequest(AppRoute.Login, AppRoute.Index)));
        SettingsCommand = CreateNavigationCommand(AppRoute.Settings);
        DownloadManagerCommand = CreateNavigationCommand(AppRoute.DownloadManager);
        ToolboxCommand = CreateNavigationCommand(AppRoute.Toolbox);
        InputCommand = new RelayCommand(QueueInput);
    }

    public string? UserName { get; }
    public string Header => "avares://DownKyi.Desktop/Resources/default_header.jpg";
    public VectorImage TextLogo { get; } = LogoIcon.TextLogo;
    public VectorImage GeneralSearch { get; } = ButtonIcon.GeneralSearch;
    public VectorImage Settings { get; } = ButtonIcon.Settings;
    public VectorImage DownloadManager { get; } = ButtonIcon.DownloadManage;
    public VectorImage Toolbox { get; } = ButtonIcon.Toolbox;

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public ICommand InputCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DownloadManagerCommand { get; }
    public ICommand ToolboxCommand { get; }

    private RelayCommand CreateNavigationCommand(AppRoute route)
    {
        return new RelayCommand(() => _queueNavigation(
            new AppNavigationRequest(route, AppRoute.Index)));
    }

    private void QueueInput()
    {
        if (!string.IsNullOrWhiteSpace(InputText))
        {
            _queueSearch(InputText);
        }
    }
}
