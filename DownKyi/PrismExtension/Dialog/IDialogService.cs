using System;
using System.Threading.Tasks;
using Prism.Dialogs;

namespace DownKyi.PrismExtension.Dialog;

internal interface IDialogService : Prism.Dialogs.IDialogService
{
    public Task ShowDialogAsync(string name, IDialogParameters? parameters, Action<IDialogResult>? callback = null,
        string? windowName = null);
}
