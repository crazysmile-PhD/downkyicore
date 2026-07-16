using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Utils;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels.Settings;

internal class ViewBasicViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsBasic";

    private readonly ISettingsStore _settingsStore;
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

    private bool _autoParseVideo;

    public bool AutoParseVideo
    {
        get => _autoParseVideo;
        set => SetProperty(ref _autoParseVideo, value);
    }

    private IReadOnlyList<ParseScopeDisplay> _parseScopes = Array.Empty<ParseScopeDisplay>();

    public IReadOnlyList<ParseScopeDisplay> ParseScopes
    {
        get => _parseScopes;
        set => SetProperty(ref _parseScopes, value);
    }

    private ParseScopeDisplay _selectedParseScope = null!;

    public ParseScopeDisplay SelectedParseScope
    {
        get => _selectedParseScope;
        set => SetProperty(ref _selectedParseScope, value);
    }

    private bool _autoDownloadAll;

    public bool AutoDownloadAll
    {
        get => _autoDownloadAll;
        set => SetProperty(ref _autoDownloadAll, value);
    }

    private bool _repeatFileAutoAddNumberSuffix;

    public bool RepeatFileAutoAddNumberSuffix
    {
        get => _repeatFileAutoAddNumberSuffix;
        set => SetProperty(ref _repeatFileAutoAddNumberSuffix, value);
    }

    private IReadOnlyList<RepeatDownloadStrategyDisplay> _repeatDownloadStrategy = Array.Empty<RepeatDownloadStrategyDisplay>();

    public IReadOnlyList<RepeatDownloadStrategyDisplay> RepeatDownloadStrategy
    {
        get => _repeatDownloadStrategy;
        set => SetProperty(ref _repeatDownloadStrategy, value);
    }

    private RepeatDownloadStrategyDisplay _selectedRepeatDownloadStrategy = null!;

    public RepeatDownloadStrategyDisplay SelectedRepeatDownloadStrategy
    {
        get => _selectedRepeatDownloadStrategy;
        set => SetProperty(ref _selectedRepeatDownloadStrategy, value);
    }

    #endregion

    public ViewBasicViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        #region 属性初始化

        // 解析范围
        ParseScopes = new List<ParseScopeDisplay>
        {
            new() { Name = DictionaryResource.GetString("ParseNone"), ParseScope = ParseScope.None },
            new() { Name = DictionaryResource.GetString("ParseSelectedItem"), ParseScope = ParseScope.SelectedItem },
            new() { Name = DictionaryResource.GetString("ParseCurrentSection"), ParseScope = ParseScope.CurrentSection },
            new() { Name = DictionaryResource.GetString("ParseAll"), ParseScope = ParseScope.All }
        };

        RepeatDownloadStrategy = new List<RepeatDownloadStrategyDisplay>
        {
            new() { Name = DictionaryResource.GetString("RepeatDownloadAsk"), RepeatDownloadStrategy = Core.Settings.RepeatDownloadStrategy.Ask },
            new() { Name = DictionaryResource.GetString("RepeatDownloadReDownload"), RepeatDownloadStrategy = Core.Settings.RepeatDownloadStrategy.ReDownload },
            new() { Name = DictionaryResource.GetString("RepeatDownloadReJumpOver"), RepeatDownloadStrategy = Core.Settings.RepeatDownloadStrategy.JumpOver }
        };

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
            case ThemeMode.Default:
                ThemeAuto = true;
                break;
        }

        // 下载完成后的操作
        var afterDownload = basic.AfterDownload;
        SetAfterDownloadOperation(afterDownload);

        // 是否监听剪贴板
        var isListenClipboard = basic.IsListenClipboard;
        ListenClipboard = isListenClipboard == AllowStatus.Yes;

        // 是否自动解析视频
        var isAutoParseVideo = basic.IsAutoParseVideo;
        AutoParseVideo = isAutoParseVideo == AllowStatus.Yes;

        // 解析范围
        var parseScope = basic.ParseScope;
        SelectedParseScope = ParseScopes.FirstOrDefault(t => t.ParseScope == parseScope) ?? ParseScopes[0];

        // 解析后是否自动下载解析视频
        var isAutoDownloadAll = basic.IsAutoDownloadAll;
        AutoDownloadAll = isAutoDownloadAll == AllowStatus.Yes;

        // 重复下载策略
        var repeatDownloadStrategy = basic.RepeatDownloadStrategy;
        SelectedRepeatDownloadStrategy = RepeatDownloadStrategy.FirstOrDefault(t => t.RepeatDownloadStrategy == repeatDownloadStrategy) ?? RepeatDownloadStrategy[0];

        // 重复下载文件自动添加数字后缀
        var repeatFileAutoAddNumberSuffix = basic.RepeatFileAutoAddNumberSuffix;
        RepeatFileAutoAddNumberSuffix = repeatFileAutoAddNumberSuffix;

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 主题事件
    private RelayCommand<string>? _themeCommand;

    public RelayCommand<string> ThemeCommand => _themeCommand ??= RequiredParameterCommand.Create<string>(ExecuteThemeCommand);

    /// <summary>
    /// 主题事件
    /// </summary>
    private void ExecuteThemeCommand(string parameter)
    {
        var themeMode = parameter switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            "Default" => ThemeMode.Default,
            _ => ThemeMode.Default
        };

        var isSucceed = UpdateBasic(settings => settings with { ThemeMode = themeMode }).ThemeMode == themeMode;
        PublishTip(isSucceed);
        ThemeHelper.SetTheme(themeMode);
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

        var isSucceed = UpdateBasic(settings => settings with { IsListenClipboard = isListenClipboard }).IsListenClipboard == isListenClipboard;
        PublishTip(isSucceed);
    }

    private RelayCommand? _autoParseVideoCommand;

    public RelayCommand AutoParseVideoCommand => _autoParseVideoCommand ??= new RelayCommand(ExecuteAutoParseVideoCommand);

    /// <summary>
    /// 是否自动解析视频
    /// </summary>
    private void ExecuteAutoParseVideoCommand()
    {
        var isAutoParseVideo = AutoParseVideo ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateBasic(settings => settings with { IsAutoParseVideo = isAutoParseVideo }).IsAutoParseVideo == isAutoParseVideo;
        PublishTip(isSucceed);
    }

    // 解析范围事件
    private RelayCommand<object>? _parseScopesCommand;

    public RelayCommand<object> ParseScopesCommand => _parseScopesCommand ??= RequiredParameterCommand.Create<object>(ExecuteParseScopesCommand);

    /// <summary>
    /// 解析范围事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteParseScopesCommand(object parameter)
    {
        if (parameter is not ParseScopeDisplay parseScope)
        {
            return;
        }

        var isSucceed = UpdateBasic(settings => settings with { ParseScope = parseScope.ParseScope }).ParseScope == parseScope.ParseScope;
        PublishTip(isSucceed);
    }

    // 解析后是否自动下载解析视频
    private RelayCommand? _autoDownloadAllCommand;

    public RelayCommand AutoDownloadAllCommand => _autoDownloadAllCommand ??= new RelayCommand(ExecuteAutoDownloadAllCommand);

    /// <summary>
    /// 解析后是否自动下载解析视频
    /// </summary>
    private void ExecuteAutoDownloadAllCommand()
    {
        var isAutoDownloadAll = AutoDownloadAll ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateBasic(settings => settings with { IsAutoDownloadAll = isAutoDownloadAll }).IsAutoDownloadAll == isAutoDownloadAll;
        PublishTip(isSucceed);
    }

    private RelayCommand? _repeatFileAutoAddNumberSuffixCommand;

    public RelayCommand RepeatFileAutoAddNumberSuffixCommand => _repeatFileAutoAddNumberSuffixCommand ??= new RelayCommand(ExecuteRepeatFileAutoAddNumberSuffixCommand);

    private void ExecuteRepeatFileAutoAddNumberSuffixCommand()
    {
        var isSucceed = UpdateBasic(settings => settings with
        {
            RepeatFileAutoAddNumberSuffix = RepeatFileAutoAddNumberSuffix
        }).RepeatFileAutoAddNumberSuffix == RepeatFileAutoAddNumberSuffix;
        PublishTip(isSucceed);
    }

    // 重复下载策略事件
    private RelayCommand<object>? _repeatDownloadStrategyCommand;

    public RelayCommand<object> RepeatDownloadStrategyCommand => _repeatDownloadStrategyCommand ??= RequiredParameterCommand.Create<object>(ExecuteRepeatDownloadStrategyCommand);

    /// <summary>
    /// 重复下载策略事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteRepeatDownloadStrategyCommand(object parameter)
    {
        if (parameter is not RepeatDownloadStrategyDisplay repeatDownloadStrategy)
        {
            return;
        }

        var isSucceed = UpdateBasic(settings => settings with
        {
            RepeatDownloadStrategy = repeatDownloadStrategy.RepeatDownloadStrategy
        }).RepeatDownloadStrategy == repeatDownloadStrategy.RepeatDownloadStrategy;
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
