using System;
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
using Microsoft.Extensions.Logging;
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

#if !DEBUG
    private SingleInstanceGuard? _singleInstanceGuard;
#endif

    private IHost? _host;
    private AvaloniaApplicationLifecycle? _applicationLifecycle;
    private ApplicationLogProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;

    public override void Initialize()
    {
#if !DEBUG
        if (!SingleInstanceGuard.TryAcquire(
                RepoOwner,
                RepoName,
                AppContext.BaseDirectory,
                out _singleInstanceGuard))
        {
            Environment.Exit(0);
        }
#endif

        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _logger?.LogCriticalMessage("Unhandled UI exception.", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                _logger?.LogCriticalMessage("Unhandled application exception.", exception);
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += OnExit!;
        }

        base.Initialize();
        _logger?.LogInformationMessage(
            $"Application initialized. Version={new AppInfo().VersionName}; Portable={StorageManager.IsPortableMode()}");
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        _logProvider = new ApplicationLogProvider(new ApplicationLogOptions(StorageManager.GetLogsDir()));
        _loggerFactory = LoggerFactory.Create(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
#else
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
#endif
            builder.AddProvider(_logProvider);
        });
        _logger = _loggerFactory.CreateLogger<App>();
        containerRegistry.RegisterInstance<IApplicationLogService>(_logProvider);
        containerRegistry.RegisterInstance<ILoggerFactory>(_loggerFactory);
        containerRegistry.Register(typeof(ILogger<>), typeof(Logger<>));
        containerRegistry.RegisterLegacyApplication();
    }


    protected override AvaloniaObject CreateShell()
    {
        _host = Container.CreateLegacyDesktopHost();
        _applicationLifecycle = Container.Resolve<IApplicationLifecycle>() as AvaloniaApplicationLifecycle
            ?? throw new InvalidOperationException("The Avalonia lifecycle adapter is not registered.");
        _applicationLifecycle.AttachHost(_host);
        var desktopContext = Container.Resolve<AvaloniaDesktopContext>();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktopContext.AttachLifetime(desktop);
        }
        else
        {
            throw new InvalidOperationException("DownKyi requires a classic desktop lifetime.");
        }

        WebClient.Configure(_host.Services.GetRequiredService<BilibiliHttpClient>());
        var shell = _host.Services.GetRequiredService<MainWindow>();
        desktopContext.AttachMainWindow(shell);
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
        _ = _applicationLifecycle?.StartHostAsync();
    }

    private void OnExit(object sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Dispose();
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
        _applicationLifecycle = null;
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        _logProvider?.Dispose();
        _logProvider = null;
        _logger = null;
#if !DEBUG
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
#endif
    }

    private async void NativeMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (_applicationLifecycle != null)
        {
            await _applicationLifecycle.ExitAsync().ConfigureAwait(true);
        }
    }

}
