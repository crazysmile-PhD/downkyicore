using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Dialogs;

internal class BaseDialogViewModel : ObservableObject
{
    #region 页面属性申明

    private string? _title;

    public string? Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private VectorImage _closeIcon = null!;

    public VectorImage CloseIcon
    {
        get => _closeIcon;
        set => SetProperty(ref _closeIcon, value);
    }

    #endregion

    public BaseDialogViewModel()
    {
        #region 属性初始化

        Title = new AppInfo().Name;
        CloseIcon = new VectorImage
        {
            Height = SystemIcon.Instance().Close.Height,
            Width = SystemIcon.Instance().Close.Width,
            Data = SystemIcon.Instance().Close.Data,
            Fill = SystemIcon.Instance().Close.Fill
        };

        #endregion
    }

    #region 命令申明

    // 鼠标进入关闭按钮事件
    private RelayCommand? _closeEnterCommand;

    public RelayCommand CloseEnterCommand => _closeEnterCommand ??= new RelayCommand(ExecuteCloseEnterCommand);

    /// <summary>
    /// 鼠标进入关闭按钮事件
    /// </summary>
    private void ExecuteCloseEnterCommand()
    {
        SetEnterStyle(CloseIcon);
    }

    // 鼠标离开关闭按钮事件
    private RelayCommand? _closeLeaveCommand;

    public RelayCommand CloseLeaveCommand => _closeLeaveCommand ??= new RelayCommand(ExecuteCloseLeaveCommand);

    /// <summary>
    /// 鼠标离开关闭按钮事件
    /// </summary>
    private void ExecuteCloseLeaveCommand()
    {
        SetLeaveStyle(CloseIcon);
    }

    // 关闭窗口事件
    private RelayCommand? _closeCommand;
    public RelayCommand CloseCommand => _closeCommand ??= new RelayCommand(ExecuteCloseCommand);

    /// <summary>
    /// 关闭窗口事件
    /// </summary>
    private void ExecuteCloseCommand()
    {
        CloseDialog(AppDialogOutcome.Canceled);
    }

    #endregion

    /// <summary>
    /// 鼠标进入系统按钮时的图标样式
    /// </summary>
    /// <param name="icon">图标</param>
    private static void SetEnterStyle(VectorImage icon)
    {
        icon.Fill = DictionaryResource.GetColor("ColorSystemBtnTint");
    }

    /// <summary>
    /// 鼠标离开系统按钮时的图标样式
    /// </summary>
    /// <param name="icon">图标</param>
    private static void SetLeaveStyle(VectorImage icon)
    {
        icon.Fill = DictionaryResource.GetColor("ColorSystemBtnTintDark");
    }

    #region 接口实现

    //触发窗体关闭事件
    protected void CloseDialog(
        AppDialogOutcome outcome,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        CloseRequested?.Invoke(this, new AppDialogResult(
            outcome,
            parameters ?? new Dictionary<string, object?>(StringComparer.Ordinal)));
    }

    public event EventHandler<AppDialogResult>? CloseRequested;

    public virtual bool CanCloseDialog()
    {
        return true;
    }

    public virtual void OnDialogClosed()
    {
    }

    public virtual void OnDialogOpened(AppDialogRequest request)
    {
    }

    protected static T GetRequiredParameter<T>(AppDialogRequest request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (request.Parameters?.TryGetValue(name, out var value) == true && value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Dialog parameter '{name}' is missing or invalid.");
    }

    #endregion
}
