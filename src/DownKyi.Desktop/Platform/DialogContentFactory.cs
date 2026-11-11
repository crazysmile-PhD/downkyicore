using System;
using Avalonia.Controls;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Platform;

internal sealed class DialogContentFactory(IServiceProvider services)
{
    private readonly IServiceProvider _services = services
        ?? throw new ArgumentNullException(nameof(services));

    public (Control Content, BaseDialogViewModel ViewModel) Create(AppDialog dialog)
    {
        var (viewType, viewModelType) = GetDialogTypes(dialog);
        var content = _services.GetRequiredService(viewType) as Control
            ?? throw new InvalidOperationException($"Dialog view '{viewType.Name}' is not a Control.");
        var viewModel = _services.GetRequiredService(viewModelType) as BaseDialogViewModel
            ?? throw new InvalidOperationException(
                $"Dialog ViewModel '{viewModelType.Name}' does not derive from BaseDialogViewModel.");
        return (content, viewModel);
    }

    internal static (Type View, Type ViewModel) GetDialogTypes(AppDialog dialog)
    {
        return dialog switch
        {
            AppDialog.Alert => (typeof(ViewAlertDialog), typeof(ViewAlertDialogViewModel)),
            AppDialog.DownloadSettings => (typeof(ViewDownloadSetter), typeof(ViewDownloadSetterViewModel)),
            AppDialog.NewVersionAvailable => (
                typeof(NewVersionAvailableDialog),
                typeof(NewVersionAvailableDialogViewModel)),
            AppDialog.LegacyUpgrade => (typeof(ViewUpgradingDialog), typeof(ViewUpgradingDialogViewModel)),
            AppDialog.StartupDiagnostics => (
                typeof(StartupDiagnosticsDialog),
                typeof(StartupDiagnosticsDialogViewModel)),
            AppDialog.DownloadContentConflict => (
                typeof(DownloadContentConflictDialog),
                typeof(DownloadContentConflictDialogViewModel)),
            AppDialog.DownloadDuplicateBatch => (
                typeof(DownloadDuplicateBatchDialog),
                typeof(DownloadDuplicateBatchDialogViewModel)),
            AppDialog.DownloadUnavailableBatch => (
                typeof(DownloadUnavailableBatchDialog),
                typeof(DownloadUnavailableBatchDialogViewModel)),
            _ => throw new ArgumentOutOfRangeException(nameof(dialog), dialog, null)
        };
    }
}
