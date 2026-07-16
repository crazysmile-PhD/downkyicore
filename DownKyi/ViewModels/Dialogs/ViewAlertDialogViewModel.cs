using System;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;

namespace DownKyi.ViewModels.Dialogs;

internal class ViewAlertDialogViewModel : BaseDialogViewModel
{
    public const string Tag = "DialogAlert";

    #region 页面属性申明

    private VectorImage _image = null!;

    public VectorImage Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }


    private bool _aloneButton;

    public bool AloneButton
    {
        get => _aloneButton;
        set => SetProperty(ref _aloneButton, value);
    }

    private bool _twoButton;

    public bool TwoButton
    {
        get => _twoButton;
        set => SetProperty(ref _twoButton, value);
    }

    #endregion

    public ViewAlertDialogViewModel()
    {
    }

    #region 命令申明

    // 确认事件
    private RelayCommand? _allowCommand;
    public RelayCommand AllowCommand => _allowCommand ??= new RelayCommand(ExecuteAllowCommand);

    /// <summary>
    /// 确认事件
    /// </summary>
    private void ExecuteAllowCommand()
    {
        CloseDialog(AppDialogOutcome.Accepted);
    }

    #endregion

    #region 接口实现

    public override void OnDialogOpened(AppDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        base.OnDialogOpened(request);

        Image = GetRequiredParameter<VectorImage>(request, "image");
        Title = GetRequiredParameter<string>(request, "title");
        Message = GetRequiredParameter<string>(request, "message");
        var number = GetRequiredParameter<int>(request, "button_number");

        switch (number)
        {
            case 1:
                AloneButton = true;
                TwoButton = false;
                break;
            case 2:
                AloneButton = false;
                TwoButton = true;
                break;
            default:
                AloneButton = false;
                TwoButton = true;
                break;
        }
    }

    #endregion
}
