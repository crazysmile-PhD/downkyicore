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

    public void AttachMainWindow(Window mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (_mainWindow != null && !ReferenceEquals(_mainWindow, mainWindow))
        {
            throw new InvalidOperationException("A different main window is already attached.");
        }

        _mainWindow = mainWindow;
    }

    public async Task ShutdownAsync()
    {
        var lifetime = _lifetime
            ?? throw new InvalidOperationException("The desktop lifetime has not been attached.");
        await Dispatcher.UIThread.InvokeAsync(() => lifetime.Shutdown());
    }
}
