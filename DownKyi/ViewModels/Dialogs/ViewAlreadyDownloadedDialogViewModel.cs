using System;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;

namespace DownKyi.ViewModels.Dialogs;

internal class ViewAlreadyDownloadedDialogViewModel : BaseDialogViewModel
{
    public const string Tag = "AlreadyDownloadedAlert";

    #region 属性声明

    private VectorImage? _image;

    public VectorImage? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    private string? _message;

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    #endregion

    public ViewAlreadyDownloadedDialogViewModel()
    {
        Title = "提示";
        Image = SystemIcon.Instance().Warning;
    }

    #region 命令声明

    private RelayCommand? _yesCommand;

    public RelayCommand YesCommand => _yesCommand ??= new RelayCommand(ExecuteYesCommand);

    private void ExecuteYesCommand()
    {
        CloseDialog(AppDialogOutcome.Accepted);
    }

    // 关闭窗口事件
    private RelayCommand? _closeCommand;
    public new RelayCommand CloseCommand => _closeCommand ??= new RelayCommand(ExecuteCloseCommand);

    /// <summary>
    /// 关闭窗口事件
    /// </summary>
    private void ExecuteCloseCommand()
    {
        CloseDialog(AppDialogOutcome.Canceled);
    }

    #endregion

    public override void OnDialogOpened(AppDialogRequest request)
    {
        Message = GetRequiredParameter<string>(request, "message");
    }
}
