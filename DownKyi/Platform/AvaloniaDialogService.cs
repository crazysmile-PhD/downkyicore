using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Platform;

internal sealed class AvaloniaDialogService : IAppDialogService
{
    private readonly IServiceProvider _services;
    private readonly AvaloniaDesktopContext _desktopContext;

    public AvaloniaDialogService(
        IServiceProvider services,
        AvaloniaDesktopContext desktopContext)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _desktopContext = desktopContext ?? throw new ArgumentNullException(nameof(desktopContext));
    }

    public async Task<AppDialogResult> ShowAsync(
        AppDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread
                .InvokeAsync(() => ShowCoreAsync(request, cancellationToken))
                .ConfigureAwait(false);
        }

        return await ShowCoreAsync(request, cancellationToken).ConfigureAwait(true);
    }

    private async Task<AppDialogResult> ShowCoreAsync(
        AppDialogRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (viewType, viewModelType) = GetDialogTypes(request.Dialog);
        var content = _services.GetRequiredService(viewType) as Control
            ?? throw new InvalidOperationException($"Dialog view '{viewType.Name}' is not a Control.");
        var viewModel = _services.GetRequiredService(viewModelType) as BaseDialogViewModel
            ?? throw new InvalidOperationException(
                $"Dialog ViewModel '{viewModelType.Name}' does not derive from BaseDialogViewModel.");
        var window = new DialogWindow
        {
            Content = content,
            DataContext = viewModel
        };
        content.DataContext = viewModel;

        var result = new AppDialogResult(
            AppDialogOutcome.Canceled,
            new Dictionary<string, object?>(StringComparer.Ordinal));
        var closeRequested = false;
        void OnCloseRequested(object? sender, AppDialogResult requestedResult)
        {
            result = requestedResult;
            closeRequested = true;
            window.Close();
        }

        void OnClosing(object? sender, WindowClosingEventArgs args)
        {
            if (!closeRequested && !viewModel.CanCloseDialog())
            {
                args.Cancel = true;
            }
        }

        viewModel.CloseRequested += OnCloseRequested;
        window.Closing += OnClosing;
        using var cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(window.Close));
        try
        {
            viewModel.OnDialogOpened(request);
            await window.ShowDialog(_desktopContext.MainWindow).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            window.Closing -= OnClosing;
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.OnDialogClosed();
            if (viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    internal static (Type View, Type ViewModel) GetDialogTypes(AppDialog dialog)
    {
        return dialog switch
        {
            AppDialog.Alert => (typeof(ViewAlertDialog), typeof(ViewAlertDialogViewModel)),
            AppDialog.DownloadSettings => (typeof(ViewDownloadSetter), typeof(ViewDownloadSetterViewModel)),
            AppDialog.ParsingSelector => (typeof(ViewParsingSelector), typeof(ViewParsingSelectorViewModel)),
            AppDialog.AlreadyDownloaded => (
                typeof(ViewAlreadyDownloadedDialog),
                typeof(ViewAlreadyDownloadedDialogViewModel)),
            AppDialog.NewVersionAvailable => (
                typeof(NewVersionAvailableDialog),
                typeof(NewVersionAvailableDialogViewModel)),
            AppDialog.LegacyUpgrade => (typeof(ViewUpgradingDialog), typeof(ViewUpgradingDialogViewModel)),
            _ => throw new ArgumentOutOfRangeException(nameof(dialog), dialog, null)
        };
    }
}
