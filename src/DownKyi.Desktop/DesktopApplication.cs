using Avalonia;
using DownKyi.Platform;

namespace DownKyi.Desktop;

public static class DesktopApplication
{
    public static void Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ProcessRestartLauncher.RunHelperIfRequested(args))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
