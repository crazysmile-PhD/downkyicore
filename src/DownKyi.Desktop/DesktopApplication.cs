using System.Runtime.Versioning;
using System.Threading;
using Avalonia;
using DownKyi.Platform;

namespace DownKyi.Desktop;

public static class DesktopApplication
{
    internal static SingleInstanceGuard? ProcessInstanceGuard { get; private set; }

    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (await ProcessRestartLauncher.RunHelperIfRequestedAsync(args).ConfigureAwait(false))
        {
            return;
        }

#if !DEBUG
        SingleInstanceGuard? processInstanceGuard = null;
#endif
        try
        {
#if !DEBUG
            if (!SingleInstanceGuard.TryAcquire(
                    AppConstant.RepoOwner,
                    AppConstant.RepoName,
                    out processInstanceGuard))
            {
                return;
            }

            ProcessInstanceGuard = processInstanceGuard;
#endif
            var appBuilder = BuildAvaloniaApp();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    await RunOnWindowsStaThreadAsync(
                            () => appBuilder.StartWithClassicDesktopLifetime(args))
                        .ConfigureAwait(false);
                }
                else
                {
                    appBuilder.StartWithClassicDesktopLifetime(args);
                }
            }
            finally
            {
                if (appBuilder.Instance is IAsyncDisposable application)
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
#if !DEBUG
            ProcessInstanceGuard = null;
            processInstanceGuard?.Dispose();
#endif
        }
    }

    [SupportedOSPlatform("windows")]
    internal static Task RunOnWindowsStaThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var uiTask = new Task(
            action,
            CancellationToken.None,
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(
            () => uiTask.RunSynchronously(TaskScheduler.Default))
        {
            IsBackground = false,
            Name = "DownKyi UI"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        return uiTask;
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
