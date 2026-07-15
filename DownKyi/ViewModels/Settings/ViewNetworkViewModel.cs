using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DownKyi.Commands;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils.Validator;
using DownKyi.Events;
using DownKyi.Services;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using Prism.Navigation.Regions;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels.Settings;

internal class ViewNetworkViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsNetwork";

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ViewNetworkViewModel> _logger;
    private bool _isOnNavigatedTo;

    #region 页面属性申明

    private bool _useSsl;

    public bool UseSsl
    {
        get => _useSsl;
        set => SetProperty(ref _useSsl, value);
    }

    private string _userAgent = string.Empty;

    public string UserAgent
    {
        get => _userAgent;
        set => SetProperty(ref _userAgent, value);
    }

    private bool _builtin;

    public bool Builtin
    {
        get => _builtin;
        set => SetProperty(ref _builtin, value);
    }

    private bool _aria2C;

    public bool Aria2C
    {
        get => _aria2C;
        set => SetProperty(ref _aria2C, value);
    }

    private bool _customAria2C;

    public bool CustomAria2C
    {
        get => _customAria2C;
        set => SetProperty(ref _customAria2C, value);
    }

    private bool _highSpeedDownloadMode;

    public bool HighSpeedDownloadMode
    {
        get => _highSpeedDownloadMode;
        set => SetProperty(ref _highSpeedDownloadMode, value);
    }

    private IReadOnlyList<int> _maxCurrentDownloads = Array.Empty<int>();

    public IReadOnlyList<int> MaxCurrentDownloads
    {
        get => _maxCurrentDownloads;
        set => SetProperty(ref _maxCurrentDownloads, value);
    }

    private int _selectedMaxCurrentDownload;

    public int SelectedMaxCurrentDownload
    {
        get => _selectedMaxCurrentDownload;
        set => SetProperty(ref _selectedMaxCurrentDownload, value);
    }

    private NetworkProxy _networkProxy;

    public NetworkProxy NetworkProxy
    {
        get => _networkProxy;
        set => SetProperty(ref _networkProxy, value);
    }

    private string? _customNetworkProxy;

    public string? CustomNetworkProxy
    {
        get => _customNetworkProxy;
        set => SetProperty(ref _customNetworkProxy, value);
    }

    private IReadOnlyList<int> _splits = Array.Empty<int>();

    public IReadOnlyList<int> Splits
    {
        get => _splits;
        set => SetProperty(ref _splits, value);
    }

    private int _selectedSplit;

    public int SelectedSplit
    {
        get => _selectedSplit;
        set => SetProperty(ref _selectedSplit, value);
    }

    private bool _isHttpProxy;

    public bool IsHttpProxy
    {
        get => _isHttpProxy;
        set => SetProperty(ref _isHttpProxy, value);
    }

    private string _httpProxy = string.Empty;

    public string HttpProxy
    {
        get => _httpProxy;
        set => SetProperty(ref _httpProxy, value);
    }

    private int _httpProxyPort;

    public int HttpProxyPort
    {
        get => _httpProxyPort;
        set => SetProperty(ref _httpProxyPort, value);
    }

    private string _ariaHost = string.Empty;

    public string AriaHost
    {
        get => _ariaHost;
        set => SetProperty(ref _ariaHost, value);
    }

    private int _ariaListenPort;

    public int AriaListenPort
    {
        get => _ariaListenPort;
        set => SetProperty(ref _ariaListenPort, value);
    }

    private string _ariaToken = string.Empty;

    public string AriaToken
    {
        get => _ariaToken;
        set => SetProperty(ref _ariaToken, value);
    }

    private IReadOnlyList<string> _ariaLogLevels = Array.Empty<string>();

    public IReadOnlyList<string> AriaLogLevels
    {
        get => _ariaLogLevels;
        set => SetProperty(ref _ariaLogLevels, value);
    }

    private string _selectedAriaLogLevel = string.Empty;

    public string SelectedAriaLogLevel
    {
        get => _selectedAriaLogLevel;
        set => SetProperty(ref _selectedAriaLogLevel, value);
    }

    private IReadOnlyList<int> _ariaMaxConcurrentDownloads = Array.Empty<int>();

    public IReadOnlyList<int> AriaMaxConcurrentDownloads
    {
        get => _ariaMaxConcurrentDownloads;
        set => SetProperty(ref _ariaMaxConcurrentDownloads, value);
    }

    private int _selectedAriaMaxConcurrentDownload;

    public int SelectedAriaMaxConcurrentDownload
    {
        get => _selectedAriaMaxConcurrentDownload;
        set => SetProperty(ref _selectedAriaMaxConcurrentDownload, value);
    }

    private IReadOnlyList<int> _ariaSplits = Array.Empty<int>();

    public IReadOnlyList<int> AriaSplits
    {
        get => _ariaSplits;
        set => SetProperty(ref _ariaSplits, value);
    }

    private int _selectedAriaSplit;

    public int SelectedAriaSplit
    {
        get => _selectedAriaSplit;
        set => SetProperty(ref _selectedAriaSplit, value);
    }

    private IReadOnlyList<int> _ariaMaxConnectionPerServers = Array.Empty<int>();

    public IReadOnlyList<int> AriaMaxConnectionPerServers
    {
        get => _ariaMaxConnectionPerServers;
        set => SetProperty(ref _ariaMaxConnectionPerServers, value);
    }

    private int _selectedAriaMaxConnectionPerServer;

    public int SelectedAriaMaxConnectionPerServer
    {
        get => _selectedAriaMaxConnectionPerServer;
        set => SetProperty(ref _selectedAriaMaxConnectionPerServer, value);
    }

    private IReadOnlyList<int> _ariaMinSplitSizes = Array.Empty<int>();

    public IReadOnlyList<int> AriaMinSplitSizes
    {
        get => _ariaMinSplitSizes;
        set => SetProperty(ref _ariaMinSplitSizes, value);
    }

    private int _selectedAriaMinSplitSize;

    public int SelectedAriaMinSplitSize
    {
        get => _selectedAriaMinSplitSize;
        set => SetProperty(ref _selectedAriaMinSplitSize, value);
    }

    private int _ariaMaxOverallDownloadLimit;

    public int AriaMaxOverallDownloadLimit
    {
        get => _ariaMaxOverallDownloadLimit;
        set => SetProperty(ref _ariaMaxOverallDownloadLimit, value);
    }

    private int _ariaMaxDownloadLimit;

    public int AriaMaxDownloadLimit
    {
        get => _ariaMaxDownloadLimit;
        set => SetProperty(ref _ariaMaxDownloadLimit, value);
    }

    private bool _isAriaHttpProxy;

    public bool IsAriaHttpProxy
    {
        get => _isAriaHttpProxy;
        set => SetProperty(ref _isAriaHttpProxy, value);
    }

    private string _ariaHttpProxy = string.Empty;

    public string AriaHttpProxy
    {
        get => _ariaHttpProxy;
        set => SetProperty(ref _ariaHttpProxy, value);
    }

    private int _ariaHttpProxyPort;

    public int AriaHttpProxyPort
    {
        get => _ariaHttpProxyPort;
        set => SetProperty(ref _ariaHttpProxyPort, value);
    }

    private IReadOnlyList<string> _ariaFileAllocations = Array.Empty<string>();

    public IReadOnlyList<string> AriaFileAllocations
    {
        get => _ariaFileAllocations;
        set => SetProperty(ref _ariaFileAllocations, value);
    }

    private string _selectedAriaFileAllocation = string.Empty;

    public string SelectedAriaFileAllocation
    {
        get => _selectedAriaFileAllocation;
        set => SetProperty(ref _selectedAriaFileAllocation, value);
    }

    #endregion

    public ViewNetworkViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        ISettingsStore settingsStore,
        ILogger<ViewNetworkViewModel> logger) : base(eventAggregator,
        dialogService)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // builtin同时下载数
        MaxCurrentDownloads = Enumerable.Range(1, 10).ToArray();

        // builtin最大线程数
        Splits = Enumerable.Range(1, 16).ToArray();

        // Aria的日志等级
        AriaLogLevels = new List<string>
        {
            "DEBUG",
            "INFO",
            "NOTICE",
            "WARN",
            "ERROR"
        };

        // Aria同时下载数
        AriaMaxConcurrentDownloads = Enumerable.Range(1, 10).ToArray();

        // Aria最大线程数
        AriaSplits = Enumerable.Range(1, 16).ToArray();

        // Aria per-server connections
        AriaMaxConnectionPerServers = Enumerable.Range(1, 16).ToArray();

        AriaMinSplitSizes = new List<int>
        {
            1,
            2,
            4,
            8,
            10,
            16,
            32,
            64
        };

        // Aria文件预分配
        AriaFileAllocations = new List<string>
        {
            "NONE",
            "PREALLOC",
            "FALLOC"
        };

        #endregion
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        _isOnNavigatedTo = true;

        // 启用https
        var network = _settingsStore.Current.Network;
        var useSsl = network.UseSsl;
        UseSsl = useSsl == AllowStatus.Yes;

        // UserAgent
        UserAgent = network.UserAgent;

        // 选择下载器
        var downloader = network.Downloader;
        switch (downloader)
        {
            case Core.Settings.Downloader.NotSet:
                break;
            case Core.Settings.Downloader.BuiltIn:
                Builtin = true;
                break;
            case Core.Settings.Downloader.Aria:
                Aria2C = true;
                break;
            case Core.Settings.Downloader.CustomAria:
                CustomAria2C = true;
                break;
        }

        NetworkProxy = network.NetworkProxy;

        CustomNetworkProxy = network.CustomNetworkProxy;

        HighSpeedDownloadMode = network.HighSpeedDownloadMode == AllowStatus.Yes;

        // builtin同时下载数
        SelectedMaxCurrentDownload = network.MaxCurrentDownloads;

        // builtin最大线程数
        SelectedSplit = network.Split;

        // 是否开启builtin http代理
        var isHttpProxy = network.IsHttpProxy;
        IsHttpProxy = isHttpProxy == AllowStatus.Yes;

        // builtin的http代理的地址
        HttpProxy = network.HttpProxy;

        // builtin的http代理的端口
        HttpProxyPort = network.HttpProxyListenPort;

        // Aria服务器host
        AriaHost = network.AriaHost;

        // Aria服务器端口
        AriaListenPort = network.AriaListenPort;

        // Aria服务器Token
        AriaToken = network.AriaToken;

        // Aria的日志等级
        var ariaLogLevel = network.AriaLogLevel;
        SelectedAriaLogLevel = ariaLogLevel.ToString("G");

        // Aria同时下载数
        SelectedAriaMaxConcurrentDownload = network.MaxCurrentDownloads;

        // Aria最大线程数
        SelectedAriaSplit = network.AriaSplit;

        SelectedAriaMaxConnectionPerServer = network.AriaMaxConnectionPerServer;

        SelectedAriaMinSplitSize = network.AriaMinSplitSize;

        // Aria下载速度限制
        AriaMaxOverallDownloadLimit = network.AriaMaxOverallDownloadLimit;

        // Aria下载单文件速度限制
        AriaMaxDownloadLimit = network.AriaMaxDownloadLimit;

        // 是否开启Aria http代理
        var isAriaHttpProxy = network.IsAriaHttpProxy;
        IsAriaHttpProxy = isAriaHttpProxy == AllowStatus.Yes;

        // Aria的http代理的地址
        AriaHttpProxy = network.AriaHttpProxy;

        // Aria的http代理的端口
        AriaHttpProxyPort = network.AriaHttpProxyListenPort;

        // Aria文件预分配
        var ariaFileAllocation = network.AriaFileAllocation;
        SelectedAriaFileAllocation = ariaFileAllocation.ToString("G");

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 是否启用https事件
    private DelegateCommand? _useSslCommand;

    public DelegateCommand UseSslCommand => _useSslCommand ??= new DelegateCommand(ExecuteUseSslCommand);

    /// <summary>
    /// 是否启用https事件
    /// </summary>
    private void ExecuteUseSslCommand()
    {
        var useSsl = UseSsl ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateNetwork(settings => settings with { UseSsl = useSsl }).UseSsl == useSsl;
        PublishTip(isSucceed);
    }

    // 设置UserAgent事件
    private DelegateCommand? _userAgentCommand;

    public DelegateCommand UserAgentCommand => _userAgentCommand ??= new DelegateCommand(ExecuteUserAgentCommand);

    /// <summary>
    /// 设置UserAgent事件
    /// </summary>
    private void ExecuteUserAgentCommand()
    {
        var isSucceed = UpdateNetwork(settings => settings with { UserAgent = UserAgent }).UserAgent == UserAgent;
        PublishTip(isSucceed);
    }

    // 下载器选择事件
    private DownKyiAsyncDelegateCommand<string>? _selectDownloaderCommand;

    public DownKyiAsyncDelegateCommand<string> SelectDownloaderCommand => _selectDownloaderCommand ??= new DownKyiAsyncDelegateCommand<string>(ExecuteSelectDownloaderCommand, _logger);

    /// <summary>
    /// 下载器选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private async Task ExecuteSelectDownloaderCommand(string? parameter)
    {
        Core.Settings.Downloader downloader;
        switch (parameter)
        {
            case "Builtin":
                downloader = Core.Settings.Downloader.BuiltIn;
                break;
            case "Aria2c":
                downloader = Core.Settings.Downloader.Aria;
                break;
            case "CustomAria2c":
                downloader = Core.Settings.Downloader.CustomAria;
                break;
            default:
                downloader = _settingsStore.Current.Network.Downloader;
                break;
        }

        var isSucceed = UpdateNetwork(settings => settings with { Downloader = downloader }).Downloader == downloader;
        PublishTip(isSucceed);

        var alertService = new AlertService(DialogService);
        var result = await alertService.ShowInfo(DictionaryResource.GetString("ConfirmReboot")).ConfigureAwait(true);
        if (result == ButtonResult.OK)
        {
            await App.Current.RequestShutdownAsync().ConfigureAwait(true);
            App.Current.AppLife?.Shutdown();
            // var dir = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            // todo 暂时去掉自动重启,多平台需要不同实现
            // if (dir != null)
            // {
            //     Process.Start($"{dir}/DownKyi");
            // }
        }
    }

    private DelegateCommand? _highSpeedDownloadModeCommand;

    public DelegateCommand HighSpeedDownloadModeCommand =>
        _highSpeedDownloadModeCommand ??= new DelegateCommand(ExecuteHighSpeedDownloadModeCommand);

    private void ExecuteHighSpeedDownloadModeCommand()
    {
        var highSpeedDownloadMode = HighSpeedDownloadMode ? AllowStatus.Yes : AllowStatus.No;

        if (HighSpeedDownloadMode)
        {
            SelectedSplit = ApplicationSettingsDefaults.HighSpeedBuiltInSplit;
            SelectedAriaSplit = ApplicationSettingsDefaults.HighSpeedAriaSplit;
            SelectedAriaMaxConnectionPerServer = ApplicationSettingsDefaults.HighSpeedAriaMaxConnectionPerServer;
            SelectedAriaMinSplitSize = ApplicationSettingsDefaults.HighSpeedAriaMinSplitSize;
        }

        var updated = UpdateNetwork(settings => settings with
        {
            HighSpeedDownloadMode = highSpeedDownloadMode,
            Split = HighSpeedDownloadMode ? SelectedSplit : settings.Split,
            AriaSplit = HighSpeedDownloadMode ? SelectedAriaSplit : settings.AriaSplit,
            AriaMaxConnectionPerServer = HighSpeedDownloadMode
                ? SelectedAriaMaxConnectionPerServer
                : settings.AriaMaxConnectionPerServer,
            AriaMinSplitSize = HighSpeedDownloadMode
                ? SelectedAriaMinSplitSize
                : settings.AriaMinSplitSize
        });
        var isSucceed = updated.HighSpeedDownloadMode == highSpeedDownloadMode
                        && (!HighSpeedDownloadMode
                            || updated.Split == SelectedSplit
                            && updated.AriaSplit == SelectedAriaSplit
                            && updated.AriaMaxConnectionPerServer == SelectedAriaMaxConnectionPerServer
                            && updated.AriaMinSplitSize == SelectedAriaMinSplitSize);

        PublishTip(isSucceed);
    }

    private DownKyiAsyncDelegateCommand<object>? _networkProxyCommand;

    public DownKyiAsyncDelegateCommand<object> NetworkProxyCommand => _networkProxyCommand ??= new DownKyiAsyncDelegateCommand<object>(ExecuteNetworkProxyCommand, _logger);

    private async Task ExecuteNetworkProxyCommand(object? obj)
    {
        if (obj is not NetworkProxy networkProxy) return;
        NetworkProxy = networkProxy;
        var isSucceed = UpdateNetwork(settings => settings with
        {
            NetworkProxy = networkProxy
        }).NetworkProxy == networkProxy;
        PublishTip(isSucceed);
        var alertService = new AlertService(DialogService);
        var result = await alertService.ShowInfo(DictionaryResource.GetString("ConfirmReboot")).ConfigureAwait(true);
        if (result == ButtonResult.OK)
        {
            await App.Current.RequestShutdownAsync().ConfigureAwait(true);
            App.Current.AppLife?.Shutdown();
        }
    }

    // builtin的http代理的地址事件
    private DelegateCommand<string>? _customNetworkProxyCommand;

    public DelegateCommand<string> CustomNetworkProxyCommand => _customNetworkProxyCommand ??= new DelegateCommand<string>(ExecuteCustomNetworkProxyCommand);

    /// <summary>
    /// builtin的http代理的地址事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteCustomNetworkProxyCommand(string parameter)
    {
        var isSucceed = UpdateNetwork(settings => settings with
        {
            CustomNetworkProxy = parameter
        }).CustomNetworkProxy == parameter;
        PublishTip(isSucceed);
    }


    // builtin同时下载数事件
    private DownKyiAsyncDelegateCommand<object>? _maxCurrentDownloadsCommand;

    public DownKyiAsyncDelegateCommand<object> MaxCurrentDownloadsCommand => _maxCurrentDownloadsCommand ??= new DownKyiAsyncDelegateCommand<object>(ExecuteMaxCurrentDownloadsCommand, _logger);

    /// <summary>
    /// builtin同时下载数事件
    /// </summary>
    /// <param name="parameter"></param>
    private async Task ExecuteMaxCurrentDownloadsCommand(object? parameter)
    {
        // SelectedMaxCurrentDownload = (int)parameter;
        if (parameter == null) return;
        var isSucceed = UpdateNetwork(settings => settings with
        {
            MaxCurrentDownloads = SelectedMaxCurrentDownload
        }).MaxCurrentDownloads == SelectedMaxCurrentDownload;
        PublishTip(isSucceed);

        var alertService = new AlertService(DialogService);
        var result = await alertService.ShowInfo(DictionaryResource.GetString("ConfirmReboot")).ConfigureAwait(true);
        if (result == ButtonResult.OK)
        {
            await App.Current.RequestShutdownAsync().ConfigureAwait(true);
            App.Current.AppLife?.Shutdown();
        }
    }

    // builtin最大线程数事件
    private DelegateCommand<object>? _splitsCommand;

    public DelegateCommand<object> SplitsCommand => _splitsCommand ??= new DelegateCommand<object>(ExecuteSplitsCommand);

    /// <summary>
    /// builtin最大线程数事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteSplitsCommand(object parameter)
    {
        // SelectedSplit = (int)parameter;

        var isSucceed = UpdateNetwork(settings => settings with { Split = SelectedSplit }).Split == SelectedSplit;
        PublishTip(isSucceed);
    }

    // 是否开启builtin http代理事件
    private DelegateCommand? _isHttpProxyCommand;

    public DelegateCommand IsHttpProxyCommand => _isHttpProxyCommand ??= new DelegateCommand(ExecuteIsHttpProxyCommand);

    /// <summary>
    /// 是否开启builtin http代理事件
    /// </summary>
    private void ExecuteIsHttpProxyCommand()
    {
        var isHttpProxy = IsHttpProxy ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateNetwork(settings => settings with { IsHttpProxy = isHttpProxy }).IsHttpProxy == isHttpProxy;

        PublishTip(isSucceed);
    }

    // builtin的http代理的地址事件
    private DelegateCommand<string>? _httpProxyCommand;

    public DelegateCommand<string> HttpProxyCommand => _httpProxyCommand ??= new DelegateCommand<string>(ExecuteHttpProxyCommand);

    /// <summary>
    /// builtin的http代理的地址事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteHttpProxyCommand(string parameter)
    {
        var isSucceed = UpdateNetwork(settings => settings with { HttpProxy = parameter }).HttpProxy == parameter;
        PublishTip(isSucceed);
    }

    // builtin的http代理的端口事件
    private DelegateCommand<string>? _httpProxyPortCommand;

    public DelegateCommand<string> HttpProxyPortCommand => _httpProxyPortCommand ??= new DelegateCommand<string>(ExecuteHttpProxyPortCommand);

    /// <summary>
    /// builtin的http代理的端口事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteHttpProxyPortCommand(string parameter)
    {
        var httpProxyPort = (int)Number.GetInt(parameter);
        HttpProxyPort = httpProxyPort;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            HttpProxyListenPort = HttpProxyPort
        }).HttpProxyListenPort == HttpProxyPort;
        PublishTip(isSucceed);
    }

    // Aria服务器host事件
    private DelegateCommand<string>? _ariaHostCommand;

    public DelegateCommand<string> AriaHostCommand => _ariaHostCommand ??= new DelegateCommand<string>(ExecuteAriaHostCommand);

    /// <summary>
    /// Aria服务器host事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaHostCommand(string parameter)
    {
        AriaHost = parameter;
        var isSucceed = UpdateNetwork(settings => settings with { AriaHost = AriaHost }).AriaHost == AriaHost;
        PublishTip(isSucceed);
    }

    // Aria服务器端口事件
    private DelegateCommand<string>? _ariaListenPortCommand;

    public DelegateCommand<string> AriaListenPortCommand => _ariaListenPortCommand ??= new DelegateCommand<string>(ExecuteAriaListenPortCommand);

    /// <summary>
    /// Aria服务器端口事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaListenPortCommand(string parameter)
    {
        var listenPort = (int)Number.GetInt(parameter);
        AriaListenPort = listenPort;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaListenPort = AriaListenPort
        }).AriaListenPort == AriaListenPort;
        PublishTip(isSucceed);
    }

    // Aria服务器token事件
    private DelegateCommand<string>? _ariaTokenCommand;

    public DelegateCommand<string> AriaTokenCommand => _ariaTokenCommand ??= new DelegateCommand<string>(ExecuteAriaTokenCommand);

    /// <summary>
    /// Aria服务器token事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaTokenCommand(string parameter)
    {
        AriaToken = parameter;
        var isSucceed = UpdateNetwork(settings => settings with { AriaToken = AriaToken }).AriaToken == AriaToken;
        PublishTip(isSucceed);
    }

    // Aria的日志等级事件
    private DelegateCommand<string>? _ariaLogLevelsCommand;

    public DelegateCommand<string> AriaLogLevelsCommand => _ariaLogLevelsCommand ??= new DelegateCommand<string>(ExecuteAriaLogLevelsCommand);

    /// <summary>
    /// Aria的日志等级事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaLogLevelsCommand(string parameter)
    {
        var ariaLogLevel = parameter switch
        {
            "DEBUG" => AriaConfigLogLevel.DEBUG,
            "INFO" => AriaConfigLogLevel.INFO,
            "NOTICE" => AriaConfigLogLevel.NOTICE,
            "WARN" => AriaConfigLogLevel.WARN,
            "ERROR" => AriaConfigLogLevel.ERROR,
            _ => AriaConfigLogLevel.INFO
        };

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaLogLevel = ariaLogLevel
        }).AriaLogLevel == ariaLogLevel;
        PublishTip(isSucceed);
    }

    // Aria同时下载数事件
    private DownKyiAsyncDelegateCommand<object>? _ariaMaxConcurrentDownloadsCommand;

    public DownKyiAsyncDelegateCommand<object> AriaMaxConcurrentDownloadsCommand =>
        _ariaMaxConcurrentDownloadsCommand ??= new DownKyiAsyncDelegateCommand<object>(ExecuteAriaMaxConcurrentDownloadsCommand, _logger);

    /// <summary>
    /// Aria同时下载数事件
    /// </summary>
    /// <param name="parameter"></param>
    private async Task ExecuteAriaMaxConcurrentDownloadsCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMaxConcurrentDownload = (int)parameter;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            MaxCurrentDownloads = SelectedAriaMaxConcurrentDownload
        }).MaxCurrentDownloads == SelectedAriaMaxConcurrentDownload;
        PublishTip(isSucceed);
        var alertService = new AlertService(DialogService);
        var result = await alertService.ShowInfo(DictionaryResource.GetString("ConfirmReboot")).ConfigureAwait(true);
        if (result == ButtonResult.OK)
        {
            await App.Current.RequestShutdownAsync().ConfigureAwait(true);
            App.Current.AppLife?.Shutdown();
        }
    }

    // Aria最大线程数事件
    private DelegateCommand<object?>? _ariaSplitsCommand;

    public DelegateCommand<object?> AriaSplitsCommand => _ariaSplitsCommand ??= new DelegateCommand<object?>(ExecuteAriaSplitsCommand);

    /// <summary>
    /// Aria最大线程数事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaSplitsCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaSplit = (int)parameter;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaSplit = SelectedAriaSplit
        }).AriaSplit == SelectedAriaSplit;
        PublishTip(isSucceed);
    }

    private DelegateCommand<object?>? _ariaMaxConnectionPerServersCommand;

    public DelegateCommand<object?> AriaMaxConnectionPerServersCommand => _ariaMaxConnectionPerServersCommand ??=
        new DelegateCommand<object?>(ExecuteAriaMaxConnectionPerServersCommand);

    private void ExecuteAriaMaxConnectionPerServersCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMaxConnectionPerServer = (int)parameter;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaMaxConnectionPerServer = SelectedAriaMaxConnectionPerServer
        }).AriaMaxConnectionPerServer == SelectedAriaMaxConnectionPerServer;
        PublishTip(isSucceed);
    }

    private DelegateCommand<object?>? _ariaMinSplitSizesCommand;

    public DelegateCommand<object?> AriaMinSplitSizesCommand => _ariaMinSplitSizesCommand ??=
        new DelegateCommand<object?>(ExecuteAriaMinSplitSizesCommand);

    private void ExecuteAriaMinSplitSizesCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMinSplitSize = (int)parameter;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaMinSplitSize = SelectedAriaMinSplitSize
        }).AriaMinSplitSize == SelectedAriaMinSplitSize;
        PublishTip(isSucceed);
    }

    // Aria下载速度限制事件
    private DelegateCommand<string>? _ariaMaxOverallDownloadLimitCommand;

    public DelegateCommand<string> AriaMaxOverallDownloadLimitCommand => _ariaMaxOverallDownloadLimitCommand ??= new DelegateCommand<string>(
        ExecuteAriaMaxOverallDownloadLimitCommand);

    /// <summary>
    /// Aria下载速度限制事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaMaxOverallDownloadLimitCommand(string parameter)
    {
        var downloadLimit = (int)Number.GetInt(parameter);
        AriaMaxOverallDownloadLimit = downloadLimit;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaMaxOverallDownloadLimit = AriaMaxOverallDownloadLimit
        }).AriaMaxOverallDownloadLimit == AriaMaxOverallDownloadLimit;
        PublishTip(isSucceed);
    }

    // Aria下载单文件速度限制事件
    private DelegateCommand<string>? _ariaMaxDownloadLimitCommand;

    public DelegateCommand<string> AriaMaxDownloadLimitCommand => _ariaMaxDownloadLimitCommand ??= new DelegateCommand<string>(ExecuteAriaMaxDownloadLimitCommand);

    /// <summary>
    /// Aria下载单文件速度限制事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaMaxDownloadLimitCommand(string parameter)
    {
        var downloadLimit = (int)Number.GetInt(parameter);
        AriaMaxDownloadLimit = downloadLimit;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaMaxDownloadLimit = AriaMaxDownloadLimit
        }).AriaMaxDownloadLimit == AriaMaxDownloadLimit;
        PublishTip(isSucceed);
    }

    // 是否开启Aria http代理事件
    private DelegateCommand? _isAriaHttpProxyCommand;

    public DelegateCommand IsAriaHttpProxyCommand => _isAriaHttpProxyCommand ??= new DelegateCommand(ExecuteIsAriaHttpProxyCommand);

    /// <summary>
    /// 是否开启Aria http代理事件
    /// </summary>
    private void ExecuteIsAriaHttpProxyCommand()
    {
        var isAriaHttpProxy = IsAriaHttpProxy ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            IsAriaHttpProxy = isAriaHttpProxy
        }).IsAriaHttpProxy == isAriaHttpProxy;
        PublishTip(isSucceed);
    }

    // Aria的http代理的地址事件
    private DelegateCommand<string>? _ariaHttpProxyCommand;

    public DelegateCommand<string> AriaHttpProxyCommand => _ariaHttpProxyCommand ??= new DelegateCommand<string>(ExecuteAriaHttpProxyCommand);

    /// <summary>
    /// Aria的http代理的地址事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaHttpProxyCommand(string parameter)
    {
        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaHttpProxy = parameter
        }).AriaHttpProxy == parameter;
        PublishTip(isSucceed);
    }

    // Aria的http代理的端口事件
    private DelegateCommand<string>? _ariaHttpProxyPortCommand;

    public DelegateCommand<string> AriaHttpProxyPortCommand => _ariaHttpProxyPortCommand ??= new DelegateCommand<string>(ExecuteAriaHttpProxyPortCommand);

    /// <summary>
    /// Aria的http代理的端口事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaHttpProxyPortCommand(string parameter)
    {
        var httpProxyPort = (int)Number.GetInt(parameter);
        AriaHttpProxyPort = httpProxyPort;

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaHttpProxyListenPort = AriaHttpProxyPort
        }).AriaHttpProxyListenPort == AriaHttpProxyPort;
        PublishTip(isSucceed);
    }

    // Aria文件预分配事件
    private DelegateCommand<string>? _ariaFileAllocationsCommand;

    public DelegateCommand<string> AriaFileAllocationsCommand => _ariaFileAllocationsCommand ??= new DelegateCommand<string>(ExecuteAriaFileAllocationsCommand);

    /// <summary>
    /// Aria文件预分配事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaFileAllocationsCommand(string parameter)
    {
        var ariaFileAllocation = parameter switch
        {
            "NONE" => AriaConfigFileAllocation.NONE,
            "PREALLOC" => AriaConfigFileAllocation.PREALLOC,
            "FALLOC" => AriaConfigFileAllocation.FALLOC,
            _ => AriaConfigFileAllocation.PREALLOC
        };

        var isSucceed = UpdateNetwork(settings => settings with
        {
            AriaFileAllocation = ariaFileAllocation
        }).AriaFileAllocation == ariaFileAllocation;
        PublishTip(isSucceed);
    }

    #endregion

    private NetworkApplicationSettings UpdateNetwork(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update)
    {
        return _settingsStore.Update(settings => settings with
        {
            Network = update(settings.Network)
        }).Network;
    }

    /// <summary>
    /// 发送需要显示的tip
    /// </summary>
    /// <param name="isSucceed"></param>
    private void PublishTip(bool isSucceed)
    {
        if (_isOnNavigatedTo)
        {
            return;
        }

        EventAggregator.GetEvent<MessageEvent>().Publish(isSucceed ? DictionaryResource.GetString("TipSettingUpdated") : DictionaryResource.GetString("TipSettingFailed"));
    }
}
