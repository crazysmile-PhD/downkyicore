using Avalonia.Threading;

namespace DownKyi.Desktop.Tests;

internal static class AvaloniaTestDispatcher
{
    public static Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureAccess();
        action();
        return Task.CompletedTask;
    }

    public static async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureAccess();
        await action().ConfigureAwait(true);
    }

    private static void EnsureAccess()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "Avalonia UI tests must use AvaloniaFact so the official headless session owns teardown.");
        }
    }
}
