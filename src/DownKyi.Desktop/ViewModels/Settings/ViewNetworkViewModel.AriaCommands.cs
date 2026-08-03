using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Commands;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils.Validator;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewNetworkViewModel
{
    // Aria服务器host事件
    private RelayCommand<string>? _ariaHostCommand;

    public RelayCommand<string> AriaHostCommand => _ariaHostCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaHostCommand);

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
    private RelayCommand<string>? _ariaListenPortCommand;

    public RelayCommand<string> AriaListenPortCommand => _ariaListenPortCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaListenPortCommand);

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
    private RelayCommand<string>? _ariaTokenCommand;

    public RelayCommand<string> AriaTokenCommand => _ariaTokenCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaTokenCommand);

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
    private RelayCommand<string>? _ariaLogLevelsCommand;

    public RelayCommand<string> AriaLogLevelsCommand => _ariaLogLevelsCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaLogLevelsCommand);

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
    private RelayCommand<object?>? _ariaSplitsCommand;

    public RelayCommand<object?> AriaSplitsCommand => _ariaSplitsCommand ??= new RelayCommand<object?>(ExecuteAriaSplitsCommand);

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

    private RelayCommand<object?>? _ariaMaxConnectionPerServersCommand;

    public RelayCommand<object?> AriaMaxConnectionPerServersCommand => _ariaMaxConnectionPerServersCommand ??=
        new RelayCommand<object?>(ExecuteAriaMaxConnectionPerServersCommand);

    private void ExecuteAriaMaxConnectionPerServersCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMaxConnectionPerServer = (int)parameter;

        ApplyNetwork(
            settings => settings with { AriaMaxConnectionPerServer = SelectedAriaMaxConnectionPerServer },
            settings => settings.AriaMaxConnectionPerServer == SelectedAriaMaxConnectionPerServer);
    }

    private RelayCommand<object?>? _ariaMinSplitSizesCommand;

    public RelayCommand<object?> AriaMinSplitSizesCommand => _ariaMinSplitSizesCommand ??=
        new RelayCommand<object?>(ExecuteAriaMinSplitSizesCommand);

    private void ExecuteAriaMinSplitSizesCommand(object? parameter)
    {
        if (parameter == null) return;
        SelectedAriaMinSplitSize = (int)parameter;

        ApplyNetwork(
            settings => settings with { AriaMinSplitSize = SelectedAriaMinSplitSize },
            settings => settings.AriaMinSplitSize == SelectedAriaMinSplitSize);
    }

    // Aria下载速度限制事件
    private RelayCommand<string>? _ariaMaxOverallDownloadLimitCommand;

    public RelayCommand<string> AriaMaxOverallDownloadLimitCommand => _ariaMaxOverallDownloadLimitCommand ??= RequiredParameterCommand.Create<string>(
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
    private RelayCommand<string>? _ariaMaxDownloadLimitCommand;

    public RelayCommand<string> AriaMaxDownloadLimitCommand => _ariaMaxDownloadLimitCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaMaxDownloadLimitCommand);

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

    // Toggle the local HTTP CONNECT proxy used by aria2 HTTPS downloads.
    private RelayCommand? _isAriaHttpProxyCommand;

    public RelayCommand IsAriaHttpProxyCommand => _isAriaHttpProxyCommand ??= new RelayCommand(ExecuteIsAriaHttpProxyCommand);

    /// <summary>
    /// Toggles the local HTTP CONNECT proxy used by aria2 HTTPS downloads.
    /// </summary>
    private void ExecuteIsAriaHttpProxyCommand()
    {
        var isAriaHttpProxy = IsAriaHttpProxy ? AllowStatus.Yes : AllowStatus.No;

        ApplyNetwork(
            settings => settings with { IsAriaHttpProxy = isAriaHttpProxy },
            settings => settings.IsAriaHttpProxy == isAriaHttpProxy);
    }

    // Apply the local aria2 HTTPS-download proxy host.
    private RelayCommand<string>? _ariaHttpProxyCommand;

    public RelayCommand<string> AriaHttpProxyCommand => _ariaHttpProxyCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaHttpProxyCommand);

    /// <summary>
    /// Applies the local aria2 HTTPS-download proxy host.
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAriaHttpProxyCommand(string parameter)
    {
        ApplyNetwork(
            settings => settings with { AriaHttpProxy = parameter },
            settings => settings.AriaHttpProxy == parameter);
    }

    // Apply the local aria2 HTTPS-download proxy port.
    private RelayCommand<string>? _ariaHttpProxyPortCommand;

    public RelayCommand<string> AriaHttpProxyPortCommand => _ariaHttpProxyPortCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaHttpProxyPortCommand);

    /// <summary>
    /// Applies the local aria2 HTTPS-download proxy port.
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
    private RelayCommand<string>? _ariaFileAllocationsCommand;

    public RelayCommand<string> AriaFileAllocationsCommand => _ariaFileAllocationsCommand ??= RequiredParameterCommand.Create<string>(ExecuteAriaFileAllocationsCommand);

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
}
