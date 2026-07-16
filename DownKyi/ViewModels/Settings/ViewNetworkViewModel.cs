using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils.Validator;
using DownKyi.Services.Settings;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewNetworkViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsNetwork";

    private readonly INetworkSettingsCoordinator _coordinator;
    private readonly ILogger<ViewNetworkViewModel> _logger;
    private bool _isOnNavigatedTo;

    public ViewNetworkViewModel(
        IDesktopInteractionContext desktopInteractions,
        INetworkSettingsCoordinator coordinator,
        ILogger<ViewNetworkViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var options = _coordinator.Options;
        MaxCurrentDownloads = options.MaxCurrentDownloads;
        Splits = options.Splits;
        AriaLogLevels = options.AriaLogLevels;
        AriaMaxConcurrentDownloads = options.AriaMaxConcurrentDownloads;
        AriaSplits = options.AriaSplits;
        AriaMaxConnectionPerServers = options.AriaMaxConnectionsPerServer;
        AriaMinSplitSizes = options.AriaMinSplitSizes;
        AriaFileAllocations = options.AriaFileAllocations;
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
        var network = _coordinator.Current;
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

        ApplyNetwork(
            settings => settings with { UseSsl = useSsl },
            settings => settings.UseSsl == useSsl);
    }

    // 设置UserAgent事件
    private DelegateCommand? _userAgentCommand;

    public DelegateCommand UserAgentCommand => _userAgentCommand ??= new DelegateCommand(ExecuteUserAgentCommand);

    /// <summary>
    /// 设置UserAgent事件
    /// </summary>
    private void ExecuteUserAgentCommand()
    {
        ApplyNetwork(
            settings => settings with { UserAgent = UserAgent },
            settings => settings.UserAgent == UserAgent);
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
                downloader = _coordinator.Current.Downloader;
                break;
        }

        await ApplyNetworkWithRestartPromptAsync(
            settings => settings with { Downloader = downloader },
            settings => settings.Downloader == downloader).ConfigureAwait(true);
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

        ApplyNetwork(
            settings => settings with
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
            },
            settings => settings.HighSpeedDownloadMode == highSpeedDownloadMode
                        && (!HighSpeedDownloadMode
                            || settings.Split == SelectedSplit
                            && settings.AriaSplit == SelectedAriaSplit
                            && settings.AriaMaxConnectionPerServer == SelectedAriaMaxConnectionPerServer
                            && settings.AriaMinSplitSize == SelectedAriaMinSplitSize));
    }

    private DownKyiAsyncDelegateCommand<object>? _networkProxyCommand;

    public DownKyiAsyncDelegateCommand<object> NetworkProxyCommand => _networkProxyCommand ??= new DownKyiAsyncDelegateCommand<object>(ExecuteNetworkProxyCommand, _logger);

    private async Task ExecuteNetworkProxyCommand(object? obj)
    {
        if (obj is not NetworkProxy networkProxy) return;
        NetworkProxy = networkProxy;
        await ApplyNetworkWithRestartPromptAsync(
            settings => settings with { NetworkProxy = networkProxy },
            settings => settings.NetworkProxy == networkProxy).ConfigureAwait(true);
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
        ApplyNetwork(
            settings => settings with { CustomNetworkProxy = parameter },
            settings => settings.CustomNetworkProxy == parameter);
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
        await ApplyNetworkWithRestartPromptAsync(
            settings => settings with { MaxCurrentDownloads = SelectedMaxCurrentDownload },
            settings => settings.MaxCurrentDownloads == SelectedMaxCurrentDownload).ConfigureAwait(true);
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

        ApplyNetwork(
            settings => settings with { Split = SelectedSplit },
            settings => settings.Split == SelectedSplit);
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

        ApplyNetwork(
            settings => settings with { IsHttpProxy = isHttpProxy },
            settings => settings.IsHttpProxy == isHttpProxy);
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
        ApplyNetwork(
            settings => settings with { HttpProxy = parameter },
            settings => settings.HttpProxy == parameter);
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

        ApplyNetwork(
            settings => settings with { HttpProxyListenPort = HttpProxyPort },
            settings => settings.HttpProxyListenPort == HttpProxyPort);
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
        ApplyNetwork(
            settings => settings with { AriaHost = AriaHost },
            settings => settings.AriaHost == AriaHost);
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

        ApplyNetwork(
            settings => settings with { AriaListenPort = AriaListenPort },
            settings => settings.AriaListenPort == AriaListenPort);
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
        ApplyNetwork(
            settings => settings with { AriaToken = AriaToken },
            settings => settings.AriaToken == AriaToken);
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

        ApplyNetwork(
            settings => settings with { AriaLogLevel = ariaLogLevel },
            settings => settings.AriaLogLevel == ariaLogLevel);
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

        await ApplyNetworkWithRestartPromptAsync(
            settings => settings with { MaxCurrentDownloads = SelectedAriaMaxConcurrentDownload },
            settings => settings.MaxCurrentDownloads == SelectedAriaMaxConcurrentDownload).ConfigureAwait(true);
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

        ApplyNetwork(
            settings => settings with { AriaSplit = SelectedAriaSplit },
            settings => settings.AriaSplit == SelectedAriaSplit);
    }

    private DelegateCommand<object?>? _ariaMaxConnectionPerServersCommand;

    public DelegateCommand<object?> AriaMaxConnectionPerServersCommand => _ariaMaxConnectionPerServersCommand ??=
        new DelegateCommand<object?>(ExecuteAriaMaxConnectionPerServersCommand);

    private void ExecuteAriaMaxConnectionPerServersCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMaxConnectionPerServer = (int)parameter;

        ApplyNetwork(
            settings => settings with { AriaMaxConnectionPerServer = SelectedAriaMaxConnectionPerServer },
            settings => settings.AriaMaxConnectionPerServer == SelectedAriaMaxConnectionPerServer);
    }

    private DelegateCommand<object?>? _ariaMinSplitSizesCommand;

    public DelegateCommand<object?> AriaMinSplitSizesCommand => _ariaMinSplitSizesCommand ??=
        new DelegateCommand<object?>(ExecuteAriaMinSplitSizesCommand);

    private void ExecuteAriaMinSplitSizesCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMinSplitSize = (int)parameter;

        ApplyNetwork(
            settings => settings with { AriaMinSplitSize = SelectedAriaMinSplitSize },
            settings => settings.AriaMinSplitSize == SelectedAriaMinSplitSize);
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

        ApplyNetwork(
            settings => settings with { AriaMaxOverallDownloadLimit = AriaMaxOverallDownloadLimit },
            settings => settings.AriaMaxOverallDownloadLimit == AriaMaxOverallDownloadLimit);
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

        ApplyNetwork(
            settings => settings with { AriaMaxDownloadLimit = AriaMaxDownloadLimit },
            settings => settings.AriaMaxDownloadLimit == AriaMaxDownloadLimit);
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

        ApplyNetwork(
            settings => settings with { IsAriaHttpProxy = isAriaHttpProxy },
            settings => settings.IsAriaHttpProxy == isAriaHttpProxy);
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
        ApplyNetwork(
            settings => settings with { AriaHttpProxy = parameter },
            settings => settings.AriaHttpProxy == parameter);
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

        ApplyNetwork(
            settings => settings with { AriaHttpProxyListenPort = AriaHttpProxyPort },
            settings => settings.AriaHttpProxyListenPort == AriaHttpProxyPort);
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

        ApplyNetwork(
            settings => settings with { AriaFileAllocation = ariaFileAllocation },
            settings => settings.AriaFileAllocation == ariaFileAllocation);
    }

    #endregion

    private void ApplyNetwork(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied)
    {
        _coordinator.Apply(update, isApplied, showFeedback: !_isOnNavigatedTo);
    }

    private Task<bool> ApplyNetworkWithRestartPromptAsync(
        Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
        Func<NetworkApplicationSettings, bool> isApplied)
    {
        return _coordinator.ApplyWithRestartPromptAsync(
            update,
            isApplied,
            showFeedback: !_isOnNavigatedTo);
    }
}
