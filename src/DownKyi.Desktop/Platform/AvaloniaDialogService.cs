using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Platform;

internal sealed class AvaloniaDialogService : IAppDialogService
{
    private const double WorkAreaMargin = 32;
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
        ConfigureWindow(window, content, request.Dialog);

        var result = new AppDialogResult(
            AppDialogOutcome.Canceled,
            new Dictionary<string, object?>(StringComparer.Ordinal));
        var closeRequested = false;
        var forcedCloseRequested = false;
        var dialogShown = false;
        void OnCloseRequested(object? sender, AppDialogResult requestedResult)
        {
            result = requestedResult;
            closeRequested = true;
            if (dialogShown)
            {
                window.Close();
            }
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
                if (dialogShown)
                {
                    window.Close();
                }
            }));
        try
        {
            viewModel.OnDialogOpened(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!closeRequested)
            {
                dialogShown = true;
                await window.ShowDialog(_desktopContext.MainWindow).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
            }

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

    private void ConfigureWindow(Window window, Control content, AppDialog dialog)
    {
        var owner = _desktopContext.MainWindow;
        var screen = owner.Screens.ScreenFromWindow(owner)
            ?? owner.Screens.ScreenFromPoint(owner.Position)
            ?? owner.Screens.Primary;
        var size = ConstrainDialogSize(
            dialog,
            screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080),
            screen?.Scaling ?? 1,
            owner.ClientSize);
        window.SizeToContent = SizeToContent.Manual;
        window.CanResize = true;
        window.MinWidth = Math.Min(dialog == AppDialog.DownloadSettings ? 480 : 360, size.Width);
        window.MinHeight = Math.Min(dialog == AppDialog.DownloadSettings ? 360 : 260, size.Height);
        window.Width = size.Width;
        window.Height = size.Height;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal static Size ConstrainDialogSize(
        AppDialog dialog,
        PixelRect workingArea,
        double scaling,
        Size ownerSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scaling, 0);
        var preferred = dialog switch
        {
            AppDialog.Alert => new Size(520, 320),
            AppDialog.DownloadSettings => new Size(680, 640),
            AppDialog.NewVersionAvailable => new Size(720, 600),
            AppDialog.LegacyUpgrade => new Size(600, 400),
            AppDialog.StartupDiagnostics => new Size(760, 680),
            AppDialog.DownloadContentConflict => new Size(680, 560),
            AppDialog.DownloadDuplicateBatch => new Size(680, 520),
            AppDialog.DownloadUnavailableBatch => new Size(720, 600),
            _ => throw new ArgumentOutOfRangeException(nameof(dialog), dialog, null)
        };
        var availableWidth = Math.Max(1, workingArea.Width / scaling - WorkAreaMargin);
        var availableHeight = Math.Max(1, workingArea.Height / scaling - WorkAreaMargin);
        return new Size(
            Math.Min(preferred.Width, Math.Min(availableWidth, Math.Max(1, ownerSize.Width - WorkAreaMargin))),
            Math.Min(preferred.Height, Math.Min(availableHeight, Math.Max(1, ownerSize.Height - WorkAreaMargin))));
    }

}
