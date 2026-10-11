using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Platform;

internal sealed class AvaloniaDialogService : IAppDialogService
{
    private const double StartupDiagnosticsWidth = 760;
    private const double StartupDiagnosticsHeight = 680;
    private const double WorkAreaMargin = 32;
    private readonly DialogContentFactory _contentFactory;
    private readonly AvaloniaDesktopContext _desktopContext;
    private readonly ILegacyUpgradeCoordinator _legacyUpgradeCoordinator;

    public AvaloniaDialogService(
        DialogContentFactory contentFactory,
        AvaloniaDesktopContext desktopContext,
        ILegacyUpgradeCoordinator legacyUpgradeCoordinator)
    {
        _contentFactory = contentFactory ?? throw new ArgumentNullException(nameof(contentFactory));
        _desktopContext = desktopContext ?? throw new ArgumentNullException(nameof(desktopContext));
        _legacyUpgradeCoordinator = legacyUpgradeCoordinator
            ?? throw new ArgumentNullException(nameof(legacyUpgradeCoordinator));
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
        if (request.Dialog == AppDialog.LegacyUpgrade
            && !await _legacyUpgradeCoordinator
                .HasMigrationSourceAsync(cancellationToken)
                .ConfigureAwait(true))
        {
            return new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>(StringComparer.Ordinal));
        }

        var (content, viewModel) = _contentFactory.Create(request.Dialog);
        var window = new DialogWindow
        {
            Content = content,
            DataContext = viewModel
        };
        content.DataContext = viewModel;
        if (request.Dialog == AppDialog.StartupDiagnostics)
        {
            ConfigureStartupDiagnosticsWindow(window, content);
        }

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

    private void ConfigureStartupDiagnosticsWindow(Window window, Control content)
    {
        var owner = _desktopContext.MainWindow;
        var screen = owner.Screens.ScreenFromWindow(owner)
            ?? owner.Screens.ScreenFromPoint(owner.Position)
            ?? owner.Screens.Primary;
        var size = screen is null
            ? new Size(StartupDiagnosticsWidth, StartupDiagnosticsHeight)
            : ConstrainStartupDiagnosticsSize(screen.WorkingArea, screen.Scaling);

        window.SizeToContent = SizeToContent.Manual;
        window.Width = size.Width;
        window.Height = size.Height;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal static Size ConstrainStartupDiagnosticsSize(
        PixelRect workingArea,
        double scaling)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scaling, 0);
        var availableWidth = Math.Max(1, workingArea.Width / scaling - WorkAreaMargin);
        var availableHeight = Math.Max(1, workingArea.Height / scaling - WorkAreaMargin);
        return new Size(
            Math.Min(StartupDiagnosticsWidth, availableWidth),
            Math.Min(StartupDiagnosticsHeight, availableHeight));
    }

}
