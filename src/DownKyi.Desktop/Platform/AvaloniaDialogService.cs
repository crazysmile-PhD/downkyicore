using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Platform;

internal sealed class AvaloniaDialogService : IAppDialogService
{
    private readonly DialogContentFactory _contentFactory;
    private readonly AvaloniaDesktopContext _desktopContext;

    public AvaloniaDialogService(
        DialogContentFactory contentFactory,
        AvaloniaDesktopContext desktopContext)
    {
        _contentFactory = contentFactory ?? throw new ArgumentNullException(nameof(contentFactory));
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
        var (content, viewModel) = _contentFactory.Create(request.Dialog);
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
        var forcedCloseRequested = false;
        void OnCloseRequested(object? sender, AppDialogResult requestedResult)
        {
            result = requestedResult;
            closeRequested = true;
            window.Close();
        }

        void OnClosing(object? sender, WindowClosingEventArgs args)
        {
            if (ShouldCancelClose(closeRequested, forcedCloseRequested, viewModel))
            {
                args.Cancel = true;
            }
        }

        viewModel.CloseRequested += OnCloseRequested;
        window.Closing += OnClosing;
        using var cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                forcedCloseRequested = true;
                window.Close();
            }));
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
            await CompleteViewModelLifecycleAsync(viewModel).ConfigureAwait(true);
        }
    }

    internal static async Task CompleteViewModelLifecycleAsync(BaseDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        try
        {
            await viewModel.OnDialogClosedAsync().ConfigureAwait(true);
        }
        finally
        {
            if (viewModel is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
            }
            else if (viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    internal static bool ShouldCancelClose(
        bool closeRequested,
        bool forcedCloseRequested,
        BaseDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return !closeRequested && !forcedCloseRequested && !viewModel.CanCloseDialog();
    }

}
