using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace DownKyi.Platform;

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
