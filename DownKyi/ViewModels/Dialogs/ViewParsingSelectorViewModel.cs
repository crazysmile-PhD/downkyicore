using System;
using System.Collections.Generic;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Utils;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels.Dialogs;

internal class ViewParsingSelectorViewModel : BaseDialogViewModel
{
    public const string Tag = "DialogParsingSelector";
    private readonly ISettingsStore _settingsStore;

    #region 页面属性申明

    private bool _isParseDefault;

    public bool IsParseDefault
    {
        get => _isParseDefault;
        set => SetProperty(ref _isParseDefault, value);
    }

    #endregion

    public ViewParsingSelectorViewModel(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        #region 属性初始化

        Title = DictionaryResource.GetString("ParsingSelector");

        // 解析范围
        var parseScope = _settingsStore.Current.Basic.ParseScope;
        IsParseDefault = parseScope != ParseScope.None;

        #endregion
    }

    #region 命令申明

    // 解析当前项事件
    private RelayCommand? _parseSelectedItemCommand;

    public RelayCommand ParseSelectedItemCommand => _parseSelectedItemCommand ??= new RelayCommand(ExecuteParseSelectedItemCommand);

    /// <summary>
    /// 解析当前项事件
    /// </summary>
    private void ExecuteParseSelectedItemCommand()
    {
        SetParseScopeSetting(ParseScope.SelectedItem);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            { "parseScope", ParseScope.SelectedItem }
        };

        CloseDialog(AppDialogOutcome.Accepted, parameters);
    }

    // 解析当前页视频事件
    private RelayCommand? _parseCurrentSectionCommand;

    public RelayCommand ParseCurrentSectionCommand => _parseCurrentSectionCommand ??= new RelayCommand(ExecuteParseCurrentSectionCommand);

    /// <summary>
    /// 解析当前页视频事件
    /// </summary>
    private void ExecuteParseCurrentSectionCommand()
    {
        SetParseScopeSetting(ParseScope.CurrentSection);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            { "parseScope", ParseScope.CurrentSection }
        };

        CloseDialog(AppDialogOutcome.Accepted, parameters);
    }

    // 解析所有视频事件
    private RelayCommand? _parseAllCommand;

    public RelayCommand ParseAllCommand => _parseAllCommand ??= new RelayCommand(ExecuteParseAllCommand);

    /// <summary>
    /// 解析所有视频事件
    /// </summary>
    private void ExecuteParseAllCommand()
    {
        SetParseScopeSetting(ParseScope.All);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            { "parseScope", ParseScope.All }
        };

        CloseDialog(AppDialogOutcome.Accepted, parameters);
    }

    #endregion

    /// <summary>
    /// 写入设置
    /// </summary>
    /// <param name="parseScope"></param>
    private void SetParseScopeSetting(ParseScope parseScope)
    {
        _settingsStore.Update(settings => settings with
        {
            Basic = settings.Basic with
            {
                ParseScope = IsParseDefault ? parseScope : ParseScope.None
            }
        });
    }
}
