using Avalonia;
using DownKyi.Platform;

namespace DownKyi.Desktop;

public static class DesktopApplication
{
    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (await ProcessRestartLauncher.RunHelperIfRequestedAsync(args).ConfigureAwait(false))
        {
            return;
        }

        var appBuilder = BuildAvaloniaApp();
        try
        {
            appBuilder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (appBuilder.Instance is IAsyncDisposable application)
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .LogToTrace()
#endif
            ;
    }
}
