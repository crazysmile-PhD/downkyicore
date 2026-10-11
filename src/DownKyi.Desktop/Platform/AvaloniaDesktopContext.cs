using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DownKyi.Platform;

internal sealed class AvaloniaDesktopContext
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private Window? _mainWindow;
    private Action? _showMainWindow;
    private Action? _prepareForShutdown;

    public Window MainWindow => _mainWindow
        ?? throw new InvalidOperationException("The main window has not been attached.");

    public void AttachLifetime(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (_lifetime != null && !ReferenceEquals(_lifetime, lifetime))
        {
            throw new InvalidOperationException("A different desktop lifetime is already attached.");
        }

        _lifetime = lifetime;
    }

    public void AttachMainWindow(
        Window mainWindow,
        Action? showMainWindow = null,
        Action? prepareForShutdown = null)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (_mainWindow != null && !ReferenceEquals(_mainWindow, mainWindow))
        {
            throw new InvalidOperationException("A different main window is already attached.");
        }

        _mainWindow = mainWindow;
        _showMainWindow = showMainWindow;
        _prepareForShutdown = prepareForShutdown;
    }

    public async Task ShowMainWindowAsync()
    {
        var mainWindow = MainWindow;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_showMainWindow != null)
            {
                _showMainWindow();
                return;
            }

            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.Activate();
        });
    }

    public async Task ShutdownAsync()
    {
        var lifetime = _lifetime
            ?? throw new InvalidOperationException("The desktop lifetime has not been attached.");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _prepareForShutdown?.Invoke();
            lifetime.Shutdown();
        });
    }
}
