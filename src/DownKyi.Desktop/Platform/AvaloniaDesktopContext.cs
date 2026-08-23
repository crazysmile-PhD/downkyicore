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

    public async Task<DesktopTerminationOutcome> ShutdownAsync(Action? afterHandoff = null)
    {
        var lifetime = _lifetime
            ?? throw new InvalidOperationException("The desktop lifetime has not been attached.");
        var handoffCompleted = false;
        var postHandoffInvoked = false;
        var operation = Dispatcher.UIThread.InvokeAsync(() =>
        {
            lifetime.Shutdown();
            handoffCompleted = true;
            if (afterHandoff != null)
            {
                postHandoffInvoked = true;
                afterHandoff();
            }
        });
        var operationTask = operation.GetTask();
        await Task.WhenAny(operationTask).ConfigureAwait(false);
        var operationFailure = GetTaskFailure(operationTask);

        return new DesktopTerminationOutcome(
            postHandoffInvoked,
            handoffCompleted ? null : operationFailure,
            handoffCompleted ? operationFailure : null);
    }

    private static Exception? GetTaskFailure(Task task)
    {
        if (task.IsCanceled)
        {
            return new TaskCanceledException(task);
        }

        if (!task.IsFaulted)
        {
            return null;
        }

        return task.Exception!.InnerExceptions.Count == 1
            ? task.Exception.InnerExceptions[0]
            : task.Exception;
    }
}

internal sealed record DesktopTerminationOutcome(
    bool PostHandoffInvoked,
    Exception? HandoffFailure,
    Exception? PostHandoffFailure);
