using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Application.Versioning;
using DownKyi.Commands;
using DownKyi.Infrastructure.Versioning;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Settings;

internal class AboutViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsAbout";
    private const string FeedbackIssueTemplate = "app_diagnostic_report.yml";

    private readonly ISettingsStore _settingsStore;
    private readonly IApplicationLogService _logService;
    private readonly ILogger<AboutViewModel> _logger;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly VersionCheckerService _versionChecker;
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

    public AboutViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        IPlatformLauncher platformLauncher,
        VersionCheckerService versionChecker,
        ILogger<AboutViewModel> logger) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _versionChecker = versionChecker ?? throw new ArgumentNullException(nameof(versionChecker));
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
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
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
        await OpenUriAsync($"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/releases").ConfigureAwait(true);
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
            var release = await _versionChecker
                .GetLatestReleaseAsync(_isReceiveBetaVersion)
                .ConfigureAwait(true);
            if (GitHubRelease.IsNullOrEmpty(release))
            {
                Notifications.Show(DictionaryResource.GetString("UpdateCheckFailed"));
                return;
            }

            if (SemanticVersionPolicy.IsNewer(release!.TagName, new AppInfo().VersionName))
            {
                await AppDialogs.ShowAsync(new AppDialogRequest(
                    AppDialog.NewVersionAvailable,
                    new Dictionary<string, object?>
                    {
                        ["release"] = release,
                        ["enableSkipVersion"] = false
                    })).ConfigureAwait(true);
            }
            else
            {
                Notifications.Show(DictionaryResource.GetString("AlreadyLatestVersion"));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningMessage("Manual update check timed out.");
            Notifications.Show(DictionaryResource.GetString("UpdateCheckTimedOut"));
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            _logger.LogErrorMessage("Manual update check failed.", e);
            Notifications.Show(DictionaryResource.GetString("UpdateCheckFailed"));
        }
    }

    // 意见反馈事件
    private DownKyiAsyncDelegateCommand? _feedbackCommand;

    public DownKyiAsyncDelegateCommand FeedbackCommand => _feedbackCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFeedbackCommand, _logger);

    /// <summary>
    /// 意见反馈事件
    /// </summary>
    internal async Task ExecuteFeedbackCommand()
    {
        try
        {
            var packagePath = await _logService.ExportFeedbackPackageAsync().ConfigureAwait(true);
            var issueUri = GitHubIssueUriBuilder.CreateForm(
                DictionaryResource.GetString("FeedbackIssueTitle", "[Bug] 用户反馈"),
                FeedbackIssueTemplate,
                "app_version",
                AppVersion,
                DictionaryResource.GetString("TruncatedFeedbackSuffix", "\n\n[预填信息已截断；请上传生成的诊断 ZIP。]"));
            await OpenUriAsync(issueUri).ConfigureAwait(true);
            await OpenFeedbackPackageFolderAsync(packagePath).ConfigureAwait(true);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogErrorMessage("Feedback package export failed.", e);
            Notifications.Show(DictionaryResource.GetString("DiagnosticLogExportFailed"));
        }
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
            Notifications.Show(DictionaryResource.GetString("OpenLogsFolderFailed"));
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
            var packagePath = await _logService.ExportFeedbackPackageAsync().ConfigureAwait(true);
            Notifications.Show(DictionaryResource.GetString("DiagnosticLogExported"));
            await OpenFeedbackPackageFolderAsync(packagePath).ConfigureAwait(true);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogErrorMessage("Diagnostic log export failed.", e);
            Notifications.Show(DictionaryResource.GetString("DiagnosticLogExportFailed"));
        }
    }

    // 是否接收测试版更新事件
    private RelayCommand? _receiveBetaVersionCommand;

    public RelayCommand ReceiveBetaVersionCommand => _receiveBetaVersionCommand ??= new RelayCommand(ExecuteReceiveBetaVersionCommand);

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
    private RelayCommand? _autoUpdateWhenLaunchCommand;

    public RelayCommand AutoUpdateWhenLaunchCommand => _autoUpdateWhenLaunchCommand ??= new RelayCommand(ExecuteAutoUpdateWhenLaunchCommand);

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

    private async Task OpenFeedbackPackageFolderAsync(string packagePath)
    {
        var directory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("Feedback package directory is unavailable.");
        if (!await _platformLauncher.OpenFolderAsync(directory).ConfigureAwait(true))
        {
            Notifications.Show(DictionaryResource.GetString("OpenFeedbackFolderFailed"));
        }
    }

    private Task OpenUriAsync(string value)
    {
        return OpenUriAsync(new Uri(value));
    }

    private async Task OpenUriAsync(Uri uri)
    {
        if (!await _platformLauncher.OpenUriAsync(uri).ConfigureAwait(true))
        {
            Notifications.Show(DictionaryResource.GetString("OpenWebPageFailed"));
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

        Notifications.Show(isSucceed
            ? DictionaryResource.GetString("TipSettingUpdated")
            : DictionaryResource.GetString("TipSettingFailed"));
    }
}
