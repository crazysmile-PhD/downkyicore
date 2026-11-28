using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class IndexViewModel : ViewModelBase, IIndexSurfaceViewModel
{
    public const string Tag = "PageIndex";
    private readonly IUserSessionCoordinator _userSessionCoordinator;
    private readonly ILogger<IndexViewModel> _logger;
    private readonly SearchService _searchService;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _userRefreshCancellation;

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


    public IndexViewModel(
        IDesktopInteractionContext desktopInteractions,
        IUserSessionCoordinator userSessionCoordinator,
        ISettingsStore settingsStore,
        SearchService searchService,
        ILogger<IndexViewModel> logger) : base(desktopInteractions)
    {
        _userSessionCoordinator = userSessionCoordinator
            ?? throw new ArgumentNullException(nameof(userSessionCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Header = "avares://DownKyi.Desktop/Resources/default_header.jpg";

        TextLogo = LogoIcon.TextLogo;

        GeneralSearch = ButtonIcon.GeneralSearch;

        Settings = ButtonIcon.Settings;

        DownloadManager = ButtonIcon.DownloadManage;

        Toolbox = ButtonIcon.Toolbox;

    }

    // 输入确认事件
    private RelayCommand? _inputCommand;
    public RelayCommand InputCommand => _inputCommand ??= new RelayCommand(ExecuteInput);

    /// <summary>
    /// 处理输入事件
    /// </summary>
    private void ExecuteInput()
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
        var user = _settingsStore.Current.User;
        Navigation.Navigate(user.IsLogin && user.Mid >= 0
            ? new AppNavigationRequest(AppRoute.MySpace, AppRoute.Index, user.Mid)
            : new AppNavigationRequest(AppRoute.Login, AppRoute.Index));
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
    /// 不是支持的格式，则保留输入并向使用者说明。
    /// </summary>
    private void EnterBili()
    {
        if (string.IsNullOrEmpty(InputText))
        {
            return;
        }

        _logger.LogDebugMessage("Processing search input.");
        if (!_searchService.BiliInput(InputText, AppRoute.Index))
        {
            Notifications.Show(DictionaryResource.GetString("TipUnsupportedSearchInput"));
            return;
        }

        InputText = string.Empty;
    }


    /// <summary>
    /// 更新用户登录信息
    /// </summary>
    private async Task UpdateUserInfoAsync()
    {
        var cancellationToken = ReplaceCancellationSource(ref _userRefreshCancellation);
        try
        {
            var snapshot = await _userSessionCoordinator
                .RefreshAsync(cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_userRefreshCancellation?.Token != cancellationToken)
            {
                return;
            }

            var user = snapshot.HasLoginFile ? snapshot.UserInfo : null;
            Header = user?.Face ?? "avares://DownKyi.Desktop/Resources/default_header.jpg";
            UserName = user?.Name;
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
            if (_userRefreshCancellation?.Token == cancellationToken)
            {
                Notifications.Show(DictionaryResource.GetString("UserSessionRefreshFailed"));
            }
        }
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        DownloadManager = ButtonIcon.DownloadManage;
        DownloadManager.Height = 27;
        DownloadManager.Width = 32;

        var user = _settingsStore.Current.User;
        UserName = user.IsLogin ? user.Name : null;
        if (!user.IsLogin)
        {
            Header = "avares://DownKyi.Desktop/Resources/default_header.jpg";
        }

        RunFireAndForget(UpdateUserInfoAsync(), nameof(UpdateUserInfoAsync), _logger);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _userRefreshCancellation);
        }

        base.Dispose(disposing);
    }

    ICommand IIndexSurfaceViewModel.InputCommand => InputCommand;
    ICommand IIndexSurfaceViewModel.LoginCommand => LoginCommand;
    ICommand IIndexSurfaceViewModel.SettingsCommand => SettingsCommand;
    ICommand IIndexSurfaceViewModel.DownloadManagerCommand => DownloadManagerCommand;
    ICommand IIndexSurfaceViewModel.ToolboxCommand => ToolboxCommand;
}
