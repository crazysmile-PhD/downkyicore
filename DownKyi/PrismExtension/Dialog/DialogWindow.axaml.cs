using Avalonia.Controls;
using Prism.Dialogs;

namespace DownKyi.PrismExtension.Dialog;

public partial class DialogWindow : Window, IDialogWindow
{
    public DialogWindow()
    {
        InitializeComponent();
    }

    public IDialogResult? Result { get; set; }
}