using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewIndexViewModel : ViewModelBase
{
    public const string Tag = "PageIndex";
    private readonly IUserSessionCoordinator _userSessionCoordinator;
    private readonly ILogger<ViewIndexViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _userRefreshCancellation;

    private bool _loginPanelVisibility;

    public bool LoginPanelVisibility
    {
        get => _loginPanelVisibility;
        set => SetProperty(ref _loginPanelVisibility, value);
    }

    private string? _userName;

    public string? UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    private string _header = string.Empty;

    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }


    private VectorImage _textLogo = new();

    public VectorImage TextLogo
    {
        get => _textLogo;
        set => SetProperty(ref _textLogo, value);
    }

    private string _inputText = string.Empty;

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    private VectorImage _generalSearch = new();

    public VectorImage GeneralSearch
    {
        get => _generalSearch;
        set => SetProperty(ref _generalSearch, value);
    }

    private VectorImage _settings = new();

    public VectorImage Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    private VectorImage _downloadManager = new();

    public VectorImage DownloadManager
    {
        get => _downloadManager;
        set => SetProperty(ref _downloadManager, value);
    }

    private VectorImage _toolbox = new();

    public VectorImage Toolbox
    {
        get => _toolbox;
        set => SetProperty(ref _toolbox, value);
    }


    public ViewIndexViewModel(
        IDesktopInteractionContext desktopInteractions,
        IUserSessionCoordinator userSessionCoordinator,
        ISettingsStore settingsStore,
        ILogger<ViewIndexViewModel> logger) : base(desktopInteractions)
    {
        _userSessionCoordinator = userSessionCoordinator
            ?? throw new ArgumentNullException(nameof(userSessionCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loginPanelVisibility = true;
        Header = "avares://DownKyi/Resources/default_header.jpg";

        TextLogo = LogoIcon.Instance().TextLogo;
        TextLogo.Fill = DictionaryResource.GetColor("ColorPrimary");

        GeneralSearch = ButtonIcon.Instance().GeneralSearch;
        GeneralSearch.Fill = DictionaryResource.GetColor("ColorPrimary");

        Settings = ButtonIcon.Instance().Settings;
        Settings.Fill = DictionaryResource.GetColor("ColorPrimary");

        DownloadManager = ButtonIcon.Instance().DownloadManage;
        DownloadManager.Fill = DictionaryResource.GetColor("ColorPrimary");

        Toolbox = ButtonIcon.Instance().Toolbox;
        Toolbox.Fill = DictionaryResource.GetColor("ColorPrimary");

    }

    // 输入确认事件
    private RelayCommand<object>? _inputCommand;
    public RelayCommand<object> InputCommand => _inputCommand ??= RequiredParameterCommand.Create<object>(ExecuteInput);

    /// <summary>
    /// 处理输入事件
    /// </summary>
    private void ExecuteInput(object param)
    {
        EnterBili();
    }

    // 登录事件
    private RelayCommand? _loginCommand;
    public RelayCommand LoginCommand => _loginCommand ??= new RelayCommand(ExecuteLogin);

    /// <summary>
    /// 进入登录页面
    /// </summary>
    private void ExecuteLogin()
    {
        if (UserName is null or "")
        {
            Navigation.Navigate(new AppNavigationRequest(
                AppRoute.Login,
                AppRoute.Index));
        }
        else
        {
            // 进入用户空间
            var userInfo = _settingsStore.Current.User;
            if (userInfo != null && userInfo.Mid != -1)
            {
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.MySpace,
                    AppRoute.Index,
                    userInfo.Mid));
            }
        }
    }

    // 进入设置页面
    private RelayCommand? _settingsCommand;

    public RelayCommand SettingsCommand => _settingsCommand ??= new RelayCommand(ExecuteSettingsCommand);

    /// <summary>
    /// 进入设置页面
    /// </summary>
    private void ExecuteSettingsCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.Settings,
            AppRoute.Index));
    }

    // 进入下载管理页面
    private RelayCommand? _downloadManagerCommand;

    public RelayCommand DownloadManagerCommand => _downloadManagerCommand ??= new RelayCommand(ExecuteDownloadManagerCommand);

    /// <summary>
    /// 进入下载管理页面
    /// </summary>
    private void ExecuteDownloadManagerCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.DownloadManager,
            AppRoute.Index));
    }

    // 进入工具箱页面
    private RelayCommand? _toolboxCommand;

    public RelayCommand ToolboxCommand => _toolboxCommand ??= new RelayCommand(ExecuteToolboxCommand);

    /// <summary>
    /// 进入工具箱页面
    /// </summary>
    private void ExecuteToolboxCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.Toolbox,
            AppRoute.Index));
    }


    /// <summary>
    /// 进入B站链接的处理逻辑，
    /// 只负责处理输入，并跳转到视频详情页。<para/>
    /// 不是支持的格式，则进入搜索页面。
    /// </summary>
    private void EnterBili()
    {
        if (string.IsNullOrEmpty(InputText))
        {
            return;
        }

        _logger.LogDebugMessage("Processing search input.");
        InputText = Regex.Replace(InputText, @"[【]*[^【]*[^】]*[】 ]", "");
        var searchService = new SearchService(_settingsStore, Navigation);
        var isSupport = searchService.BiliInput(InputText, AppRoute.Index);
        if (!isSupport)
        {
            // 关键词搜索
            SearchService.SearchKey(InputText, AppRoute.Index);
        }

        InputText = string.Empty;
    }


    /// <summary>
    /// 更新用户登录信息
    /// </summary>
    private async Task UpdateUserInfoAsync(bool isBackground = false)
    {
        var cancellationToken = ReplaceCancellationSource(ref _userRefreshCancellation);
        var updateUi = !isBackground || !LoginPanelVisibility;
        try
        {
            if (updateUi)
            {
                LoginPanelVisibility = false;
            }

            var snapshot = await _userSessionCoordinator
                .RefreshAsync(cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_userRefreshCancellation?.Token != cancellationToken || !updateUi)
            {
                return;
            }

            // 检查本地是否存在login文件，没有则说明未登录
            if (!snapshot.HasLoginFile)
            {
                LoginPanelVisibility = true;
                Header = "avares://DownKyi/Resources/default_header.jpg";
                UserName = null;
                return;
            }

            LoginPanelVisibility = true;

            if (snapshot.UserInfo != null)
            {
                Header = snapshot.UserInfo.Face ?? "avares://DownKyi/Resources/default_header.jpg";

                UserName = snapshot.UserInfo.Name;
            }
            else
            {
                Header = "avares://DownKyi/Resources/default_header.jpg";
                UserName = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or FormatException or System.Net.Http.HttpRequestException
            or System.Security.Cryptography.CryptographicException
            or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("User session refresh failed.", e);
            if (updateUi && _userRefreshCancellation?.Token == cancellationToken)
            {
                LoginPanelVisibility = true;
            }
        }
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        DownloadManager = ButtonIcon.Instance().DownloadManage;
        DownloadManager.Height = 27;
        DownloadManager.Width = 32;
        DownloadManager.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 根据传入参数不同执行不同任务
        var parameter = navigationContext.Parameters.GetValue<string>("Parameter");
        switch (parameter)
        {
            case null:
                // 其他情况只更新设置的用户信息，不更新UI
                RunFireAndForget(UpdateUserInfoAsync(true), nameof(UpdateUserInfoAsync), _logger);
                return;
            // 启动
            case "start":
            // 从登录页面返回
            case "login":
            // 注销
            case "logout":
                RunFireAndForget(UpdateUserInfoAsync(), nameof(UpdateUserInfoAsync), _logger);
                break;
            default:
                // 其他情况只更新设置的用户信息，不更新UI
                RunFireAndForget(UpdateUserInfoAsync(true), nameof(UpdateUserInfoAsync), _logger);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _userRefreshCancellation);
        }

        base.Dispose(disposing);
    }
}
