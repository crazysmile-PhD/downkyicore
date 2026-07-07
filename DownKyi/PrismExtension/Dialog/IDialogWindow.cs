using Avalonia.Styling;

namespace DownKyi.PrismExtension.Dialog;

public interface IDialogWindow: Prism.Dialogs.IDialogWindow
{
    ControlTheme? Theme { get; set; }
}