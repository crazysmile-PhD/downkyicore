using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Commands;
using DownKyi.Core.Utils.Validator;
using DownKyi.Services.Settings;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Settings;

internal partial class NetworkViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsNetwork";

    private readonly INetworkSettingsCoordinator _coordinator;
    private readonly ILogger<NetworkViewModel> _logger;
    private bool _isOnNavigatedTo;

    public NetworkViewModel(
        IDesktopInteractionContext desktopInteractions,
        INetworkSettingsCoordinator coordinator,
        ILogger<NetworkViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var options = _coordinator.Options;
        MaxCurrentDownloads = options.MaxCurrentDownloads;
        Splits = options.Splits;
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

        NetworkProxy = network.NetworkProxy;

        CustomNetworkProxy = network.CustomNetworkProxy;

        HighSpeedDownloadMode = network.HighSpeedDownloadMode == AllowStatus.Yes;

        // builtin同时下载数
        SelectedMaxCurrentDownload = network.MaxCurrentDownloads;

        // builtin最大线程数
        SelectedSplit = network.Split;

        MaxOverallDownloadLimit = network.MaxOverallDownloadLimit;

        MaxDownloadLimit = network.MaxDownloadLimit;

        // 是否开启builtin http代理
        var isHttpProxy = network.IsHttpProxy;
        IsHttpProxy = isHttpProxy == AllowStatus.Yes;

        // builtin的http代理的地址
        HttpProxy = network.HttpProxy;

        // builtin的http代理的端口
        HttpProxyPort = network.HttpProxyListenPort;

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

    private RelayCommand? _highSpeedDownloadModeCommand;

    public RelayCommand HighSpeedDownloadModeCommand =>
        _highSpeedDownloadModeCommand ??= new RelayCommand(ExecuteHighSpeedDownloadModeCommand);

    private void ExecuteHighSpeedDownloadModeCommand()
    {
        var highSpeedDownloadMode = HighSpeedDownloadMode ? AllowStatus.Yes : AllowStatus.No;

        if (HighSpeedDownloadMode)
        {
            SelectedSplit = ApplicationSettingsDefaults.HighSpeedBuiltInSplit;
        }

        ApplyNetwork(
            settings => settings with
            {
                HighSpeedDownloadMode = highSpeedDownloadMode,
                Split = HighSpeedDownloadMode ? SelectedSplit : settings.Split
            },
            settings => settings.HighSpeedDownloadMode == highSpeedDownloadMode
                        && (!HighSpeedDownloadMode
                            || settings.Split == SelectedSplit));
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

    private RelayCommand<string>? _maxOverallDownloadLimitCommand;

    public RelayCommand<string> MaxOverallDownloadLimitCommand =>
        _maxOverallDownloadLimitCommand ??= RequiredParameterCommand.Create<string>(
            ExecuteMaxOverallDownloadLimitCommand);

    private void ExecuteMaxOverallDownloadLimitCommand(string parameter)
    {
        MaxOverallDownloadLimit = ParseDownloadLimit(parameter);
        ApplyNetwork(
            settings => settings with
            {
                MaxOverallDownloadLimit = MaxOverallDownloadLimit
            },
            settings => settings.MaxOverallDownloadLimit == MaxOverallDownloadLimit);
    }

    private RelayCommand<string>? _maxDownloadLimitCommand;

    public RelayCommand<string> MaxDownloadLimitCommand =>
        _maxDownloadLimitCommand ??= RequiredParameterCommand.Create<string>(
            ExecuteMaxDownloadLimitCommand);

    private void ExecuteMaxDownloadLimitCommand(string parameter)
    {
        MaxDownloadLimit = ParseDownloadLimit(parameter);
        ApplyNetwork(
            settings => settings with { MaxDownloadLimit = MaxDownloadLimit },
            settings => settings.MaxDownloadLimit == MaxDownloadLimit);
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

    private static int ParseDownloadLimit(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var result = 0;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return 0;
            }

            var digit = character - '0';
            if (result > (int.MaxValue - digit) / 10)
            {
                return int.MaxValue;
            }

            result = (result * 10) + digit;
        }

        return result;
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
