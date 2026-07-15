using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Application.Time;
using DownKyi.Composition;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Download;
using DownKyi.Services.Friends;
using DownKyi.Services.Media;
using DownKyi.Services.Migration;
using DownKyi.Services.Toolbox;
using DownKyi.Services.UserSpace;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using DownKyi.Views;
using DownKyi.Views.Dialogs;
using DownKyi.Views.DownloadManager;
using DownKyi.Views.Friends;
using DownKyi.Views.Settings;
using DownKyi.Views.Toolbox;
using DownKyi.Views.UserSpace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prism.DryIoc;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation.Regions;
using ViewSeasonsSeries = DownKyi.Views.ViewSeasonsSeries;
using ViewSeasonsSeriesViewModel = DownKyi.ViewModels.ViewSeasonsSeriesViewModel;

namespace DownKyi;

internal partial class App : PrismApplication, IDisposable
{
    private bool _disposed;
    public const string RepoOwner = "crazysmile-PhD";
    public const string RepoName = "downkyicore";

    public new static App Current => (App)Avalonia.Application.Current!;
    public new MainWindow MainWindow => _host?.Services.GetRequiredService<MainWindow>()
        ?? throw new InvalidOperationException("The application host has not been created.");
    public IClassicDesktopStyleApplicationLifetime? AppLife { get; private set; }
#if !DEBUG
    private static Mutex? _mutex;
#endif

    private IHost? _host;
    private Task? _downloadStartupTask;
    private Task? _shutdownTask;

    public override void Initialize()
    {
#if !DEBUG
        _mutex = new Mutex(true, BuildSingleInstanceMutexName(), out var createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
        }
#endif

        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, e) => { LogManager.Error("[Program crash]", e.Exception); };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            LogManager.Error("[Program crash]", exception!);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += OnExit!;
            AppLife = desktop;
        }

        LogManager.Info(nameof(App), $"Application initialized. Version={new AppInfo().VersionName}; Portable={StorageManager.IsPortableMode()}");

        base.Initialize();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterLegacyApplication();
    }


    protected override AvaloniaObject CreateShell()
    {
        _host = Container.CreateLegacyDesktopHost();
        WebClient.Configure(_host.Services.GetRequiredService<BilibiliHttpClient>());
        var shell = _host.Services.GetRequiredService<MainWindow>();
        shell.AttachLegacyRegion();
        if (!Design.IsDesignMode)
        {
            Dispatcher.UIThread.Post(StartDownloadBootstrap, DispatcherPriority.Background);
        }

        return shell;
    }

    protected override void OnInitialized()
    {
        ThemeHelper.SetTheme(Container.Resolve<ISettingsStore>().Current.Basic.ThemeMode);
        // var regionManager = Container.Resolve<IRegionManager>();
        // regionManager.RegisterViewWithRegion("ContentRegion", typeof(ViewIndex));
        // regionManager.RegisterViewWithRegion("DownloadManagerContentRegion", typeof(ViewDownloading));
        // regionManager.RegisterViewWithRegion("SettingsContentRegion", typeof(ViewBasic));
    }

    private void StartDownloadBootstrap()
    {
        _downloadStartupTask ??= StartHostAsync(GetShutdownToken());
    }

    private async Task StartHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_host != null)
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // App 正在关闭时允许取消。
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            LogManager.Error(nameof(App), e);
        }
    }

    private void OnExit(object sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_shutdownTask is { IsCompleted: false })
        {
            LogManager.Info(nameof(App), "Application lifetime exited before asynchronous cleanup completed.");
        }

        Dispose();
    }

    internal Task RequestShutdownAsync()
    {
        return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        var cleanupTasks = new List<Task>();
        if (_host != null)
        {
            await _host.Services
                .GetRequiredService<ApplicationCancellation>()
                .RequestShutdownAsync()
                .ConfigureAwait(false);
            cleanupTasks.Add(StopHostAsync(_host));
        }

        cleanupTasks.Add(Container.Resolve<ISettingsStore>().FlushAsync());

        if (_downloadStartupTask != null)
        {
            cleanupTasks.Add(_downloadStartupTask);
        }

        var cleanup = Task.WhenAll(cleanupTasks);
        if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == cleanup)
        {
            await cleanup.ConfigureAwait(false);
        }
        else
        {
            LogManager.Info(nameof(App), "Application cleanup timed out; killing the tracked aria2 process.");
            AriaServer.KillTrackedServer("application exit cleanup timed out.");
            _ = cleanup.ContinueWith(
                task => LogManager.Error(nameof(App), task.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        await LogManager.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static Task StopHostAsync(IHost host)
    {
        return host.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        _host?.Dispose();
        _host = null;
#if !DEBUG
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process is exiting; only avoid masking the original shutdown path.
        }

        _mutex?.Dispose();
        _mutex = null;
#endif
    }

    private async void NativeMenuItem_OnClick(object? sender, EventArgs e)
    {
        await RequestShutdownAsync().ConfigureAwait(true);
        AppLife?.Shutdown();
    }

    private static string BuildSingleInstanceMutexName()
    {
        var installPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installPath)).AsSpan(0, 8));
        return $@"Global\DownKyi-{RepoOwner}-{RepoName}-{hash}";
    }

    private CancellationToken GetShutdownToken()
    {
        return _host?.Services.GetRequiredService<ApplicationCancellation>().ShutdownToken
            ?? throw new InvalidOperationException("The application cancellation service has not been created.");
    }

}
