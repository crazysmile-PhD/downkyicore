using Avalonia.Styling;

namespace DownKyi.PrismExtension.Dialog;

internal interface IDialogWindow : Prism.Dialogs.IDialogWindow
{
    ControlTheme? Theme { get; set; }
}
