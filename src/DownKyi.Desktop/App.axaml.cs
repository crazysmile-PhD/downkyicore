using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.Desktop;
using DownKyi.Desktop.Appearance;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Logging;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi;

internal partial class App : Avalonia.Application, IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    private IHost? _host;
    private AvaloniaApplicationLifecycle? _applicationLifecycle;
    private ApplicationLogProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;
    private AvaloniaDesktopContext? _desktopContext;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _showMainWindowMenuItem;
    private NativeMenuItem? _exitApplicationMenuItem;
    private bool _unhandledExceptionLoggingAttached;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        CreateHost();
        AttachUnhandledExceptionLogging();
        _logger?.LogInformationMessage(
            $"Application initialized. Version={new AppInfo().VersionName}; Portable={ApplicationStorage.IsPortableMode()}");
        if (_logProvider != null)
        {
            ObserveBackgroundTask(
                _logProvider.FlushAsync(CancellationToken.None),
                "Application initialization log flush failed.");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("DownKyi requires a classic desktop lifetime.");
        }

        var host = _host ?? throw new InvalidOperationException("The application Host is not initialized.");
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _applicationLifecycle = host.Services.GetRequiredService<AvaloniaApplicationLifecycle>();
        _applicationLifecycle.AttachHost(host);

        _desktopContext = host.Services.GetRequiredService<AvaloniaDesktopContext>();
        _desktopContext.AttachLifetime(desktop);
        var imageLoader = host.Services.GetRequiredService<IAsyncImageLoader>();
        ImageLoader.AsyncImageLoader = imageLoader;
        ImageBrushLoader.AsyncImageLoader = imageLoader;

        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        _desktopContext.AttachMainWindow(
            mainWindow,
            mainWindow.ShowAndActivate,
            mainWindow.PrepareForApplicationExit);
        desktop.MainWindow = mainWindow;
        ConfigureTrayIcon();
        DesktopApplication.ProcessInstanceGuard?.SetActivationHandler(ShowMainWindow);

        host.Services.GetRequiredService<DesktopThemeController>().ApplySavedMode();
        host.Services.GetRequiredService<MainWindowViewModel>().PrepareForFirstShow();
        base.OnFrameworkInitializationCompleted();

        if (!Design.IsDesignMode)
        {
            Dispatcher.UIThread.Post(StartHost, DispatcherPriority.Background);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_applicationLifecycle != null)
            {
                await _applicationLifecycle
                    .RequestShutdownAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            DesktopApplication.ProcessInstanceGuard?.SetActivationHandler(null);
            DetachTrayIcon();
            try
            {
                if (_host is IAsyncDisposable asyncHost)
                {
                    await asyncHost.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    _host?.Dispose();
                }
            }
            finally
            {
                _host = null;
                _applicationLifecycle = null;
                _desktopContext = null;
                DetachUnhandledExceptionLogging();
                try
                {
                    _loggerFactory?.Dispose();
                }
                finally
                {
                    _loggerFactory = null;
                    try
                    {
                        if (_logProvider != null)
                        {
                            await _logProvider.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _logProvider = null;
                        _logger = null;
                    }
                }
            }
        }
    }

    private void CreateHost()
    {
        _logProvider = new ApplicationLogProvider(new ApplicationLogOptions(ApplicationStorage.GetLogsDir()));
        _loggerFactory = LoggerFactory.Create(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.SetMinimumLevel(LogLevel.Information);
#endif
            builder.AddProvider(_logProvider);
        });
        _logger = _loggerFactory.CreateLogger<App>();
        _host = DownKyiHost.Create(services =>
            services.AddDownKyiDesktop(_loggerFactory, _logProvider));
    }

    private void AttachUnhandledExceptionLogging()
    {
        if (_unhandledExceptionLoggingAttached)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        _unhandledExceptionLoggingAttached = true;
    }

    private void DetachUnhandledExceptionLogging()
    {
        if (!_unhandledExceptionLoggingAttached)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException -= OnUiUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        _unhandledExceptionLoggingAttached = false;
    }

    private void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCriticalMessage("Unhandled UI exception.", e.Exception);
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.LogCriticalMessage("Unhandled application exception.", exception);
        }
    }

    private void StartHost()
    {
        var applicationLifecycle = _applicationLifecycle;
        if (applicationLifecycle != null)
        {
            ObserveBackgroundTask(
                Task.Run(applicationLifecycle.StartHostAsync),
                "Application Host startup failed.");
        }
    }

    private void ConfigureTrayIcon()
    {
        var trayIcons = TrayIcon.GetIcons(this);
        if (trayIcons == null || trayIcons.Count == 0)
        {
            return;
        }

        _trayIcon = trayIcons[0];
        _showMainWindowMenuItem = new NativeMenuItem(DictionaryResource.GetString("ShowMainWindow"));
        _exitApplicationMenuItem = new NativeMenuItem(DictionaryResource.GetString("ExitApplication"));
        _showMainWindowMenuItem.Click += OnShowMainWindowRequested;
        _exitApplicationMenuItem.Click += OnExitApplicationRequested;
        _trayIcon.Clicked += OnShowMainWindowRequested;
        _trayIcon.Menu = new NativeMenu
        {
            Items =
            {
                _showMainWindowMenuItem,
                new NativeMenuItemSeparator(),
                _exitApplicationMenuItem
            }
        };
    }

    private void DetachTrayIcon()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Clicked -= OnShowMainWindowRequested;
        }

        if (_showMainWindowMenuItem != null)
        {
            _showMainWindowMenuItem.Click -= OnShowMainWindowRequested;
        }

        if (_exitApplicationMenuItem != null)
        {
            _exitApplicationMenuItem.Click -= OnExitApplicationRequested;
        }

        _trayIcon = null;
        _showMainWindowMenuItem = null;
        _exitApplicationMenuItem = null;
    }

    private void OnShowMainWindowRequested(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_desktopContext != null)
        {
            ObserveBackgroundTask(
                _desktopContext.ShowMainWindowAsync(),
                "Showing the main window failed.");
        }
    }

    private void OnExitApplicationRequested(object? sender, EventArgs e)
    {
        if (_applicationLifecycle != null)
        {
            ObserveBackgroundTask(
                _applicationLifecycle.ExitAsync(),
                "Application exit failed.");
        }
    }

    private void ObserveBackgroundTask(Task task, string failureMessage)
    {
        _ = task.ContinueWith(
            completed => _logger?.LogErrorMessage(
                failureMessage,
                completed.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
