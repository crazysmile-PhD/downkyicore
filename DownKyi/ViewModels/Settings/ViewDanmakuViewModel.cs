using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils.Validator;
using DownKyi.Utils;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels.Settings;

internal class ViewDanmakuViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsDanmaku";

    private readonly ISettingsStore _settingsStore;
    private bool _isOnNavigatedTo;

    #region 页面属性申明

    private bool _topFilter;

    public bool TopFilter
    {
        get => _topFilter;
        set => SetProperty(ref _topFilter, value);
    }

    private bool _bottomFilter;

    public bool BottomFilter
    {
        get => _bottomFilter;
        set => SetProperty(ref _bottomFilter, value);
    }

    private bool _scrollFilter;

    public bool ScrollFilter
    {
        get => _scrollFilter;
        set => SetProperty(ref _scrollFilter, value);
    }

    private int _screenWidth;

    public int ScreenWidth
    {
        get => _screenWidth;
        set => SetProperty(ref _screenWidth, value);
    }

    private int _screenHeight;

    public int ScreenHeight
    {
        get => _screenHeight;
        set => SetProperty(ref _screenHeight, value);
    }

    private IReadOnlyList<string> _fonts = Array.Empty<string>();

    public IReadOnlyList<string> Fonts
    {
        get => _fonts;
        set => SetProperty(ref _fonts, value);
    }

    private string _selectedFont = string.Empty;

    public string SelectedFont
    {
        get => _selectedFont;
        set => SetProperty(ref _selectedFont, value);
    }

    private int _fontSize;

    public int FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }

    private int _lineCount;

    public int LineCount
    {
        get => _lineCount;
        set => SetProperty(ref _lineCount, value);
    }

    private bool _sync;

    public bool Sync
    {
        get => _sync;
        set => SetProperty(ref _sync, value);
    }

    private bool _async;

    public bool Async
    {
        get => _async;
        set => SetProperty(ref _async, value);
    }

    #endregion

    public ViewDanmakuViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        #region 属性初始化

        // 弹幕字体
        Fonts = FontManager.Current.SystemFonts.Select(x => x.Name).ToList();

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

        // 屏蔽顶部弹幕
        var danmaku = _settingsStore.Current.Danmaku;
        var danmakuTopFilter = danmaku.TopFilter;
        TopFilter = danmakuTopFilter == AllowStatus.Yes;

        // 屏蔽底部弹幕
        var danmakuBottomFilter = danmaku.BottomFilter;
        BottomFilter = danmakuBottomFilter == AllowStatus.Yes;

        // 屏蔽滚动弹幕
        var danmakuScrollFilter = danmaku.ScrollFilter;
        ScrollFilter = danmakuScrollFilter == AllowStatus.Yes;

        // 分辨率-宽
        ScreenWidth = danmaku.ScreenWidth;

        // 分辨率-高
        ScreenHeight = danmaku.ScreenHeight;

        // 弹幕字体
        var danmakuFont = danmaku.FontName;
        if (danmakuFont != null && Fonts.Contains(danmakuFont))
        {
            // 只有系统中存在当前设置的字体，才能显示
            SelectedFont = danmakuFont;
        }

        // 弹幕字体大小
        FontSize = danmaku.FontSize;

        // 弹幕限制行数
        LineCount = danmaku.LineCount;

        // 弹幕布局算法
        var layoutAlgorithm = danmaku.LayoutAlgorithm;
        SetLayoutAlgorithm(layoutAlgorithm);

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 屏蔽顶部弹幕事件
    private RelayCommand? _topFilterCommand;

    public RelayCommand TopFilterCommand => _topFilterCommand ??= new RelayCommand(ExecuteTopFilterCommand);

    /// <summary>
    /// 屏蔽顶部弹幕事件
    /// </summary>
    private void ExecuteTopFilterCommand()
    {
        var isTopFilter = TopFilter ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateDanmaku(settings => settings with { TopFilter = isTopFilter }).TopFilter == isTopFilter;
        PublishTip(isSucceed);
    }

    // 屏蔽底部弹幕事件
    private RelayCommand? _bottomFilterCommand;

    public RelayCommand BottomFilterCommand => _bottomFilterCommand ??= new RelayCommand(ExecuteBottomFilterCommand);

    /// <summary>
    /// 屏蔽底部弹幕事件
    /// </summary>
    private void ExecuteBottomFilterCommand()
    {
        var isBottomFilter = BottomFilter ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateDanmaku(settings => settings with { BottomFilter = isBottomFilter }).BottomFilter == isBottomFilter;
        PublishTip(isSucceed);
    }

    // 屏蔽滚动弹幕事件
    private RelayCommand? _scrollFilterCommand;

    public RelayCommand ScrollFilterCommand => _scrollFilterCommand ??= new RelayCommand(ExecuteScrollFilterCommand);

    /// <summary>
    /// 屏蔽滚动弹幕事件
    /// </summary>
    private void ExecuteScrollFilterCommand()
    {
        var isScrollFilter = ScrollFilter ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateDanmaku(settings => settings with { ScrollFilter = isScrollFilter }).ScrollFilter == isScrollFilter;
        PublishTip(isSucceed);
    }

    // 设置分辨率-宽事件
    private RelayCommand<string>? _screenWidthCommand;

    public RelayCommand<string> ScreenWidthCommand => _screenWidthCommand ??= RequiredParameterCommand.Create<string>(ExecuteScreenWidthCommand);

    /// <summary>
    /// 设置分辨率-宽事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteScreenWidthCommand(string parameter)
    {
        var width = (int)Number.GetInt(parameter);
        ScreenWidth = width;

        var isSucceed = UpdateDanmaku(settings => settings with { ScreenWidth = ScreenWidth }).ScreenWidth == ScreenWidth;
        PublishTip(isSucceed);
    }

    // 设置分辨率-高事件
    private RelayCommand<string>? _screenHeightCommand;

    public RelayCommand<string> ScreenHeightCommand => _screenHeightCommand ??= RequiredParameterCommand.Create<string>(ExecuteScreenHeightCommand);

    /// <summary>
    /// 设置分辨率-高事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteScreenHeightCommand(string parameter)
    {
        var height = (int)Number.GetInt(parameter);
        ScreenHeight = height;

        var isSucceed = UpdateDanmaku(settings => settings with { ScreenHeight = ScreenHeight }).ScreenHeight == ScreenHeight;
        PublishTip(isSucceed);
    }

    // 弹幕字体选择事件
    private RelayCommand<string>? _fontSelectCommand;

    public RelayCommand<string> FontSelectCommand => _fontSelectCommand ??= RequiredParameterCommand.Create<string>(ExecuteFontSelectCommand);

    /// <summary>
    /// 弹幕字体选择事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteFontSelectCommand(string parameter)
    {
        var isSucceed = UpdateDanmaku(settings => settings with { FontName = parameter }).FontName == parameter;
        PublishTip(isSucceed);
    }

    // 弹幕字体大小事件
    private RelayCommand<string>? _fontSizeCommand;

    public RelayCommand<string> FontSizeCommand => _fontSizeCommand ??= RequiredParameterCommand.Create<string>(ExecuteFontSizeCommand);

    /// <summary>
    /// 弹幕字体大小事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteFontSizeCommand(string parameter)
    {
        var fontSize = (int)Number.GetInt(parameter);
        FontSize = fontSize;

        var isSucceed = UpdateDanmaku(settings => settings with { FontSize = FontSize }).FontSize == FontSize;
        PublishTip(isSucceed);
    }

    // 弹幕限制行数事件
    private RelayCommand<string>? _lineCountCommand;

    public RelayCommand<string> LineCountCommand => _lineCountCommand ??= RequiredParameterCommand.Create<string>(ExecuteLineCountCommand);

    /// <summary>
    /// 弹幕限制行数事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteLineCountCommand(string parameter)
    {
        var lineCount = (int)Number.GetInt(parameter);
        LineCount = lineCount;

        var isSucceed = UpdateDanmaku(settings => settings with { LineCount = LineCount }).LineCount == LineCount;
        PublishTip(isSucceed);
    }

    // 弹幕布局算法事件
    private RelayCommand<string>? _layoutAlgorithmCommand;

    public RelayCommand<string> LayoutAlgorithmCommand => _layoutAlgorithmCommand ??= RequiredParameterCommand.Create<string>(ExecuteLayoutAlgorithmCommand);

    /// <summary>
    /// 弹幕布局算法事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteLayoutAlgorithmCommand(string parameter)
    {
        var layoutAlgorithm = parameter switch
        {
            "Sync" => DanmakuLayoutAlgorithm.Sync,
            "Async" => DanmakuLayoutAlgorithm.Async,
            _ => DanmakuLayoutAlgorithm.Sync
        };

        var isSucceed = UpdateDanmaku(settings => settings with
        {
            LayoutAlgorithm = layoutAlgorithm
        }).LayoutAlgorithm == layoutAlgorithm;
        PublishTip(isSucceed);

        if (isSucceed)
        {
            SetLayoutAlgorithm(layoutAlgorithm);
        }
    }

    #endregion

    private DanmakuApplicationSettings UpdateDanmaku(
        Func<DanmakuApplicationSettings, DanmakuApplicationSettings> update)
    {
        return _settingsStore.Update(settings => settings with
        {
            Danmaku = update(settings.Danmaku)
        }).Danmaku;
    }

    /// <summary>
    /// 设置弹幕同步算法
    /// </summary>
    /// <param name="layoutAlgorithm"></param>
    private void SetLayoutAlgorithm(DanmakuLayoutAlgorithm layoutAlgorithm)
    {
        switch (layoutAlgorithm)
        {
            case DanmakuLayoutAlgorithm.Sync:
                Sync = true;
                Async = false;
                break;
            case DanmakuLayoutAlgorithm.Async:
                Sync = false;
                Async = true;
                break;
            case DanmakuLayoutAlgorithm.None:
                Sync = false;
                Async = false;
                break;
            default:
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
