using System;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Desktop.Appearance;
using DownKyi.Platform;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Settings;

internal class BasicViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsBasic";

    private readonly ISettingsStore _settingsStore;
    private readonly DesktopThemeController _themeController;
    private readonly IClipboardMonitor _clipboardMonitor;
    private bool _isOnNavigatedTo;

    #region 页面属性申明

    private bool _themeLight;

    public bool ThemeLight
    {
        get => _themeLight;
        set => SetProperty(ref _themeLight, value);
    }

    private bool _themeDark;

    public bool ThemeDark
    {
        get => _themeDark;
        set => SetProperty(ref _themeDark, value);
    }

    private bool _themeAuto;

    public bool ThemeAuto
    {
        get => _themeAuto;
        set => SetProperty(ref _themeAuto, value);
    }

    private bool _languageChineseSimplified;
    public bool LanguageChineseSimplified
    {
        get => _languageChineseSimplified;
        set => SetProperty(ref _languageChineseSimplified, value);
    }

    private bool _languageChineseTraditional;
    public bool LanguageChineseTraditional
    {
        get => _languageChineseTraditional;
        set => SetProperty(ref _languageChineseTraditional, value);
    }

    private bool _languageEnglish;
    public bool LanguageEnglish
    {
        get => _languageEnglish;
        set => SetProperty(ref _languageEnglish, value);
    }

    private bool _languageJapanese;
    public bool LanguageJapanese
    {
        get => _languageJapanese;
        set => SetProperty(ref _languageJapanese, value);
    }

    private bool _none;

    public bool None
    {
        get => _none;
        set => SetProperty(ref _none, value);
    }

    private bool _closeApp;

    public bool CloseApp
    {
        get => _closeApp;
        set => SetProperty(ref _closeApp, value);
    }

    private bool _closeSystem;

    public bool CloseSystem
    {
        get => _closeSystem;
        set => SetProperty(ref _closeSystem, value);
    }

    private bool _listenClipboard;

    public bool ListenClipboard
    {
        get => _listenClipboard;
        set => SetProperty(ref _listenClipboard, value);
    }

    #endregion

    public BasicViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore,
        DesktopThemeController themeController,
        IClipboardMonitor clipboardMonitor) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _themeController = themeController ?? throw new ArgumentNullException(nameof(themeController));
        _clipboardMonitor = clipboardMonitor ?? throw new ArgumentNullException(nameof(clipboardMonitor));
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        _isOnNavigatedTo = true;

        // 主题
        var basic = _settingsStore.Current.Basic;
        var themeMode = basic.ThemeMode;
        switch (themeMode)
        {
            case ThemeMode.Light:
                ThemeLight = true;
                break;
            case ThemeMode.Dark:
                ThemeDark = true;
                break;
            case ThemeMode.None:
            case ThemeMode.Default:
                ThemeAuto = true;
                break;
        }

        LanguageChineseSimplified = basic.UiLanguage == UiLanguage.ChineseSimplified;
        LanguageChineseTraditional = basic.UiLanguage == UiLanguage.ChineseTraditional;
        LanguageEnglish = basic.UiLanguage == UiLanguage.English;
        LanguageJapanese = basic.UiLanguage == UiLanguage.Japanese;

        // 下载完成后的操作
        var afterDownload = basic.AfterDownload;
        SetAfterDownloadOperation(afterDownload);

        // 是否监听剪贴板
        var isListenClipboard = basic.IsListenClipboard;
        ListenClipboard = isListenClipboard == AllowStatus.Yes;

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 主题事件
    private RelayCommand<ThemeMode>? _themeCommand;

    public RelayCommand<ThemeMode> ThemeCommand =>
        _themeCommand ??= new RelayCommand<ThemeMode>(ExecuteThemeCommand);

    /// <summary>
    /// 主题事件
    /// </summary>
    private void ExecuteThemeCommand(ThemeMode themeMode)
    {
        var isSucceed = _themeController.SetMode(themeMode);
        PublishTip(isSucceed);
    }

    private RelayCommand<UiLanguage>? _languageCommand;

    public RelayCommand<UiLanguage> LanguageCommand =>
        _languageCommand ??= new RelayCommand<UiLanguage>(ExecuteLanguageCommand);

    private void ExecuteLanguageCommand(UiLanguage language)
    {
        var updated = UpdateBasic(settings => settings with { UiLanguage = language });
        Notifications.Show(DictionaryResource.GetString(updated.UiLanguage == language
            ? "RestartToApplyLanguage"
            : "TipSettingFailed"));
    }

    // 下载完成后的操作事件
    private RelayCommand<string>? _afterDownloadOperationCommand;

    public RelayCommand<string> AfterDownloadOperationCommand => _afterDownloadOperationCommand ??= RequiredParameterCommand.Create<string>(ExecuteAfterDownloadOperationCommand);

    /// <summary>
    /// 下载完成后的操作事件
    /// </summary>
    private void ExecuteAfterDownloadOperationCommand(string parameter)
    {
        AfterDownloadOperation afterDownload;
        switch (parameter)
        {
            case "None":
                afterDownload = AfterDownloadOperation.None;
                break;
            case "CloseApp":
                afterDownload = AfterDownloadOperation.CloseApp;
                break;
            case "CloseSystem":
                afterDownload = AfterDownloadOperation.CloseSystem;
                break;
            default:
                afterDownload = AfterDownloadOperation.None;
                break;
        }

        var isSucceed = UpdateBasic(settings => settings with { AfterDownload = afterDownload }).AfterDownload == afterDownload;
        PublishTip(isSucceed);
    }

    // 是否监听剪贴板事件
    private RelayCommand? _listenClipboardCommand;

    public RelayCommand ListenClipboardCommand => _listenClipboardCommand ??= new RelayCommand(ExecuteListenClipboardCommand);

    /// <summary>
    /// 是否监听剪贴板事件
    /// </summary>
    private void ExecuteListenClipboardCommand()
    {
        var isListenClipboard = ListenClipboard ? AllowStatus.Yes : AllowStatus.No;

        var actual = UpdateBasic(settings => settings with { IsListenClipboard = isListenClipboard }).IsListenClipboard;
        _clipboardMonitor.SetEnabled(actual == AllowStatus.Yes);
        var isSucceed = actual == isListenClipboard;
        PublishTip(isSucceed);
    }

    #endregion

    private BasicApplicationSettings UpdateBasic(
        Func<BasicApplicationSettings, BasicApplicationSettings> update)
    {
        return _settingsStore.Update(settings => settings with
        {
            Basic = update(settings.Basic)
        }).Basic;
    }

    /// <summary>
    /// 设置下载完成后的操作
    /// </summary>
    /// <param name="afterDownload"></param>
    private void SetAfterDownloadOperation(AfterDownloadOperation afterDownload)
    {
        switch (afterDownload)
        {
            case AfterDownloadOperation.None:
                None = true;
                break;
            case AfterDownloadOperation.OpenFolder:
                break;
            case AfterDownloadOperation.CloseApp:
                CloseApp = true;
                break;
            case AfterDownloadOperation.CloseSystem:
                CloseSystem = true;
                break;
        }
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

        Notifications.Show(isSucceed ? DictionaryResource.GetString("TipSettingUpdated") : DictionaryResource.GetString("TipSettingFailed"));
    }
}
