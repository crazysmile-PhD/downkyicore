using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils.Validator;
using DownKyi.Services.Settings;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

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
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        _isOnNavigatedTo = true;

        var network = _coordinator.Current;

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

        // Whether to use a local HTTP CONNECT proxy for aria2 HTTPS downloads.
        var isAriaHttpProxy = network.IsAriaHttpProxy;
        IsAriaHttpProxy = isAriaHttpProxy == AllowStatus.Yes;

        // Local aria2 HTTPS-download proxy host.
        AriaHttpProxy = network.AriaHttpProxy;

        // Local aria2 HTTPS-download proxy port.
        AriaHttpProxyPort = network.AriaHttpProxyListenPort;

        // Aria文件预分配
        var ariaFileAllocation = network.AriaFileAllocation;
        SelectedAriaFileAllocation = ariaFileAllocation.ToString("G");

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 设置UserAgent事件
    private RelayCommand? _userAgentCommand;

    public RelayCommand UserAgentCommand => _userAgentCommand ??= new RelayCommand(ExecuteUserAgentCommand);

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

    private RelayCommand? _highSpeedDownloadModeCommand;

    public RelayCommand HighSpeedDownloadModeCommand =>
        _highSpeedDownloadModeCommand ??= new RelayCommand(ExecuteHighSpeedDownloadModeCommand);

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
    private RelayCommand<string>? _customNetworkProxyCommand;

    public RelayCommand<string> CustomNetworkProxyCommand => _customNetworkProxyCommand ??= RequiredParameterCommand.Create<string>(ExecuteCustomNetworkProxyCommand);

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
    private RelayCommand<object>? _splitsCommand;

    public RelayCommand<object> SplitsCommand => _splitsCommand ??= RequiredParameterCommand.Create<object>(ExecuteSplitsCommand);

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
    private RelayCommand? _isHttpProxyCommand;

    public RelayCommand IsHttpProxyCommand => _isHttpProxyCommand ??= new RelayCommand(ExecuteIsHttpProxyCommand);

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
    private RelayCommand<string>? _httpProxyCommand;

    public RelayCommand<string> HttpProxyCommand => _httpProxyCommand ??= RequiredParameterCommand.Create<string>(ExecuteHttpProxyCommand);

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
    private RelayCommand<string>? _httpProxyPortCommand;

    public RelayCommand<string> HttpProxyPortCommand => _httpProxyPortCommand ??= RequiredParameterCommand.Create<string>(ExecuteHttpProxyPortCommand);

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
