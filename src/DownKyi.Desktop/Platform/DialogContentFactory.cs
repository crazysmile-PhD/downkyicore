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
        // The window completes this model's async lifetime; the root provider must not retain it.
        var viewModel = ActivatorUtilities.CreateInstance(_services, viewModelType) as BaseDialogViewModel
            ?? throw new InvalidOperationException(
                $"Dialog ViewModel '{viewModelType.Name}' does not derive from BaseDialogViewModel.");
        return (content, viewModel);
    }

    internal static (Type View, Type ViewModel) GetDialogTypes(AppDialog dialog)
    {
        return dialog switch
        {
            AppDialog.Alert => (typeof(AlertDialog), typeof(AlertDialogViewModel)),
            AppDialog.DownloadSettings => (typeof(DownloadSetterDialog), typeof(DownloadSetterViewModel)),
            AppDialog.NewVersionAvailable => (
                typeof(NewVersionAvailableDialog),
                typeof(NewVersionAvailableDialogViewModel)),
            AppDialog.LegacyUpgrade => (typeof(UpgradingDialog), typeof(UpgradingDialogViewModel)),
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
