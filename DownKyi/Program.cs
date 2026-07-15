using System;
using System.Threading.Tasks;
using Avalonia;
using DownKyi.Platform;

namespace DownKyi;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static async Task Main(string[] args)
    {
        if (await ProcessRestartLauncher.RunHelperIfRequestedAsync(args).ConfigureAwait(false))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .LogToTrace()
#endif
            ;
}
