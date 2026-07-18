using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace DownKyi.Desktop.Tests;

internal static class HeadlessUiTestHost
{
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly Lazy<Task> Initialization = new(StartAsync);

    public static async Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public static async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Initialization.Value.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(action).ConfigureAwait(false);
    }

    private static Task StartAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunDispatcher(started))
        {
            IsBackground = true,
            Name = "DownKyi headless UI tests"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        return started.Task;
    }

    private static void RunDispatcher(TaskCompletionSource started)
    {
        try
        {
            AppBuilder
                .Configure<SmokeTestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            started.TrySetResult();
            Dispatcher.UIThread.MainLoop(Shutdown.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or PlatformNotSupportedException
            or TypeInitializationException)
        {
            started.TrySetException(exception);
        }
    }

    private sealed class SmokeTestApplication : Avalonia.Application
    {
    }
}
