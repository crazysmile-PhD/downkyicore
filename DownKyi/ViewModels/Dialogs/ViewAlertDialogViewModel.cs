using System;
using DownKyi.Images;
using Prism.Commands;
using Prism.Dialogs;

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
    private DelegateCommand? _allowCommand;
    public DelegateCommand AllowCommand => _allowCommand ??= new DelegateCommand(ExecuteAllowCommand);

    /// <summary>
    /// 确认事件
    /// </summary>
    private void ExecuteAllowCommand()
    {
        ButtonResult result = ButtonResult.OK;
        CloseDialog(new DialogResult(result));
    }

    #endregion

    #region 接口实现

    public override void OnDialogOpened(IDialogParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        base.OnDialogOpened(parameters);

        Image = parameters.GetValue<VectorImage>("image");
        Title = parameters.GetValue<string>("title");
        Message = parameters.GetValue<string>("message");
        var number = parameters.GetValue<int>("button_number");

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
