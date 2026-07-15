using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Events;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using Prism.Navigation.Regions;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels.Settings;

internal class ViewAboutViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsAbout";

    private readonly ISettingsStore _settingsStore;
    private readonly IApplicationLogService _logService;
    private readonly ILogger<ViewAboutViewModel> _logger;
    private readonly IPlatformLauncher _platformLauncher;
    private bool _isOnNavigatedTo;

    #region 页面属性申明

    private string _appName = string.Empty;

    public string AppName
    {
        get => _appName;
        set => SetProperty(ref _appName, value);
    }

    private string _appVersion = string.Empty;

    public string AppVersion
    {
        get => _appVersion;
        set => SetProperty(ref _appVersion, value);
    }

    private bool _isReceiveBetaVersion;

    public bool IsReceiveBetaVersion
    {
        get => _isReceiveBetaVersion;
        set => SetProperty(ref _isReceiveBetaVersion, value);
    }

    private bool _autoUpdateWhenLaunch;

    public bool AutoUpdateWhenLaunch
    {
        get => _autoUpdateWhenLaunch;
        set => SetProperty(ref _autoUpdateWhenLaunch, value);
    }

    #endregion

    public ViewAboutViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        IPlatformLauncher platformLauncher,
        ILogger<ViewAboutViewModel> logger) : base(eventAggregator,
        dialogService)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        var app = new AppInfo();
        AppName = app.Name;
        AppVersion = app.VersionName;

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

        // 是否接收测试版更新
        var about = _settingsStore.Current.About;
        var isReceiveBetaVersion = about.IsReceiveBetaVersion;
        IsReceiveBetaVersion = isReceiveBetaVersion == AllowStatus.Yes;

        // 是否在启动时自动检查更新
        var isAutoUpdateWhenLaunch = about.AutoUpdateWhenLaunch;
        AutoUpdateWhenLaunch = isAutoUpdateWhenLaunch == AllowStatus.Yes;

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 访问主页事件
    private DownKyiAsyncDelegateCommand? _appNameCommand;

    public DownKyiAsyncDelegateCommand AppNameCommand => _appNameCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAppNameCommand, _logger);

    /// <summary>
    /// 访问主页事件
    /// </summary>
    private async Task ExecuteAppNameCommand()
    {
        await OpenUriAsync($"https://github.com/{App.RepoOwner}/{App.RepoName}/releases").ConfigureAwait(true);
    }

    // 检查更新事件
    private ICommand? _checkUpdateCommand;

    public ICommand CheckUpdateCommand => _checkUpdateCommand ??= new DownKyiAsyncDelegateCommand(ExecuteCheckUpdateCommand, _logger);


    /// <summary>
    /// 检查更新事件
    /// </summary>
    private async Task ExecuteCheckUpdateCommand()
    {
        try
        {
            var service = new VersionCheckerService(App.RepoOwner, App.RepoName, _isReceiveBetaVersion);
            var release = await service.GetLatestReleaseAsync().ConfigureAwait(true);
            if (GitHubRelease.IsNullOrEmpty(release))
            {
                EventAggregator.GetEvent<MessageEvent>().Publish("检查失败，请稍后重试~");
                return;
            }

            if (service.IsNewVersionAvailable(release!.TagName))
            {
                var dialogService = DialogService ?? throw new InvalidOperationException("Dialog service is not available.");
                await dialogService.ShowDialogAsync(NewVersionAvailableDialogViewModel.Tag, new
                    DialogParameters { { "release", release } }).ConfigureAwait(true);
            }
            else
            {
                EventAggregator.GetEvent<MessageEvent>().Publish("已是最新版~");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningMessage("Manual update check timed out.");
            EventAggregator.GetEvent<MessageEvent>().Publish("检查超时，请稍后重试~");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            _logger.LogErrorMessage("Manual update check failed.", e);
            EventAggregator.GetEvent<MessageEvent>().Publish("检查失败，请稍后重试~");
        }
    }

    // 意见反馈事件
    private DownKyiAsyncDelegateCommand? _feedbackCommand;

    public DownKyiAsyncDelegateCommand FeedbackCommand => _feedbackCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFeedbackCommand, _logger);

    /// <summary>
    /// 意见反馈事件
    /// </summary>
    private async Task ExecuteFeedbackCommand()
    {
        await OpenUriAsync($"https://github.com/{App.RepoOwner}/{App.RepoName}/issues").ConfigureAwait(true);
    }

    // 打开日志目录事件
    private DownKyiAsyncDelegateCommand? _openLogsCommand;

    public DownKyiAsyncDelegateCommand OpenLogsCommand => _openLogsCommand ??= new DownKyiAsyncDelegateCommand(ExecuteOpenLogsCommand, _logger);

    private async Task ExecuteOpenLogsCommand()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _logService.FlushAsync(cancellation.Token).ConfigureAwait(true);
        if (!await _platformLauncher.OpenFolderAsync(_logService.LogDirectory).ConfigureAwait(true))
        {
            EventAggregator.GetEvent<MessageEvent>().Publish("无法打开日志文件夹");
        }
    }

    // 导出脱敏诊断日志事件
    private DownKyiAsyncDelegateCommand? _exportDiagnosticLogCommand;

    public DownKyiAsyncDelegateCommand ExportDiagnosticLogCommand =>
        _exportDiagnosticLogCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExportDiagnosticLogCommand, _logger);

    private async Task ExecuteExportDiagnosticLogCommand()
    {
        try
        {
            var diagnosticLog = await _logService.ExportDiagnosticLogAsync().ConfigureAwait(true);
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("DiagnosticLogExported"));
            if (!await _platformLauncher.OpenFileAsync(diagnosticLog).ConfigureAwait(true))
            {
                EventAggregator.GetEvent<MessageEvent>().Publish("无法打开诊断日志");
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogErrorMessage("Diagnostic log export failed.", e);
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("DiagnosticLogExportFailed"));
        }
    }

    // 是否接收测试版更新事件
    private DelegateCommand? _receiveBetaVersionCommand;

    public DelegateCommand ReceiveBetaVersionCommand => _receiveBetaVersionCommand ??= new DelegateCommand(ExecuteReceiveBetaVersionCommand);

    /// <summary>
    /// 是否接收测试版更新事件
    /// </summary>
    private void ExecuteReceiveBetaVersionCommand()
    {
        var isReceiveBetaVersion = IsReceiveBetaVersion ? AllowStatus.Yes : AllowStatus.No;

        var updated = _settingsStore.Update(settings => settings with
        {
            About = settings.About with { IsReceiveBetaVersion = isReceiveBetaVersion }
        });
        var isSucceed = updated.About.IsReceiveBetaVersion == isReceiveBetaVersion;
        PublishTip(isSucceed);
    }

    // 是否在启动时自动检查更新事件
    private DelegateCommand? _autoUpdateWhenLaunchCommand;

    public DelegateCommand AutoUpdateWhenLaunchCommand => _autoUpdateWhenLaunchCommand ??= new DelegateCommand(ExecuteAutoUpdateWhenLaunchCommand);

    /// <summary>
    /// 是否在启动时自动检查更新事件
    /// </summary>
    private void ExecuteAutoUpdateWhenLaunchCommand()
    {
        var isAutoUpdateWhenLaunch = AutoUpdateWhenLaunch ? AllowStatus.Yes : AllowStatus.No;

        var updated = _settingsStore.Update(settings => settings with
        {
            About = settings.About with { AutoUpdateWhenLaunch = isAutoUpdateWhenLaunch }
        });
        var isSucceed = updated.About.AutoUpdateWhenLaunch == isAutoUpdateWhenLaunch;
        PublishTip(isSucceed);
    }

    // Google.Protobuf许可证查看事件
    private DownKyiAsyncDelegateCommand? _protobufLicenseCommand;

    public DownKyiAsyncDelegateCommand ProtobufLicenseCommand => _protobufLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteProtobufLicenseCommand, _logger);

    /// <summary>
    /// Google.Protobuf许可证查看事件
    /// </summary>
    private async Task ExecuteProtobufLicenseCommand()
    {
        await OpenUriAsync("https://github.com/protocolbuffers/protobuf/blob/master/LICENSE").ConfigureAwait(true);
    }

    // Newtonsoft.Json许可证查看事件
    private DownKyiAsyncDelegateCommand? _newtonsoftLicenseCommand;

    public DownKyiAsyncDelegateCommand NewtonsoftLicenseCommand => _newtonsoftLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteNewtonsoftLicenseCommand, _logger);

    /// <summary>
    /// Newtonsoft.Json许可证查看事件
    /// </summary>
    private async Task ExecuteNewtonsoftLicenseCommand()
    {
        await OpenUriAsync("https://licenses.nuget.org/MIT").ConfigureAwait(true);
    }

    // Prism.DryIoc许可证查看事件
    private DownKyiAsyncDelegateCommand? _prismLicenseCommand;

    public DownKyiAsyncDelegateCommand PrismLicenseCommand => _prismLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecutePrismLicenseCommand, _logger);

    /// <summary>
    /// Prism.DryIoc许可证查看事件
    /// </summary>
    private async Task ExecutePrismLicenseCommand()
    {
        await OpenUriAsync("https://www.nuget.org/packages/Prism.DryIoc/8.1.97/license").ConfigureAwait(true);
    }

    // QRCoder许可证查看事件
    private DownKyiAsyncDelegateCommand? _qRCoderLicenseCommand;

    public DownKyiAsyncDelegateCommand QRCoderLicenseCommand => _qRCoderLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteQRCoderLicenseCommand, _logger);

    /// <summary>
    /// QRCoder许可证查看事件
    /// </summary>
    private async Task ExecuteQRCoderLicenseCommand()
    {
        await OpenUriAsync("https://licenses.nuget.org/MIT").ConfigureAwait(true);
    }

    // System.Data.SQLite.Core许可证查看事件
    private DownKyiAsyncDelegateCommand? _sQLiteLicenseCommand;

    public DownKyiAsyncDelegateCommand SQLiteLicenseCommand => _sQLiteLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteSQLiteLicenseCommand, _logger);

    /// <summary>
    /// System.Data.SQLite.Core许可证查看事件
    /// </summary>
    private async Task ExecuteSQLiteLicenseCommand()
    {
        await OpenUriAsync("https://www.sqlite.org/copyright.html").ConfigureAwait(true);
    }

    // Aria2c许可证查看事件
    private DownKyiAsyncDelegateCommand? _ariaLicenseCommand;

    public DownKyiAsyncDelegateCommand AriaLicenseCommand => _ariaLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAriaLicenseCommand, _logger);

    /// <summary>
    /// Aria2c许可证查看事件
    /// </summary>
    private async Task ExecuteAriaLicenseCommand()
    {
        if (!await _platformLauncher.OpenFileAsync("aria2_COPYING.txt").ConfigureAwait(true))
        {
            EventAggregator.GetEvent<MessageEvent>().Publish("无法打开 aria2 许可文件");
        }
    }

    // FFmpeg许可证查看事件
    private DownKyiAsyncDelegateCommand? _fFmpegLicenseCommand;

    public DownKyiAsyncDelegateCommand FFmpegLicenseCommand => _fFmpegLicenseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFFmpegLicenseCommand, _logger);

    /// <summary>
    /// FFmpeg许可证查看事件
    /// </summary>
    private async Task ExecuteFFmpegLicenseCommand()
    {
        if (!await _platformLauncher.OpenFileAsync("FFmpeg_LICENSE.txt").ConfigureAwait(true))
        {
            EventAggregator.GetEvent<MessageEvent>().Publish("无法打开 FFmpeg 许可文件");
        }
    }

    private async Task OpenUriAsync(string value)
    {
        if (!await _platformLauncher.OpenUriAsync(new Uri(value)).ConfigureAwait(true))
        {
            EventAggregator.GetEvent<MessageEvent>().Publish("无法打开网页");
        }
    }

    #endregion

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

        EventAggregator.GetEvent<MessageEvent>().Publish(isSucceed
            ? DictionaryResource.GetString("TipSettingUpdated")
            : DictionaryResource.GetString("TipSettingFailed"));
    }
}
