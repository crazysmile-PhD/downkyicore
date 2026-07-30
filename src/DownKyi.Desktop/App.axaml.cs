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
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Logging;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Utils;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi;

internal partial class App : Avalonia.Application, IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

#if !DEBUG
    private SingleInstanceGuard? _singleInstanceGuard;
#endif

    private IHost? _host;
    private AvaloniaApplicationLifecycle? _applicationLifecycle;
    private ApplicationLogProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;
    private bool _unhandledExceptionLoggingAttached;

    public override void Initialize()
    {
#if !DEBUG
        if (!SingleInstanceGuard.TryAcquire(
                AppConstant.RepoOwner,
                AppConstant.RepoName,
                AppContext.BaseDirectory,
                out _singleInstanceGuard))
        {
            Environment.Exit(0);
        }
#endif

        AvaloniaXamlLoader.Load(this);
        CreateHost();
        AttachUnhandledExceptionLogging();
        _logger?.LogInformationMessage(
            $"Application initialized. Version={new AppInfo().VersionName}; Portable={ApplicationStorage.IsPortableMode()}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("DownKyi requires a classic desktop lifetime.");
        }

        var host = _host ?? throw new InvalidOperationException("The application Host is not initialized.");
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        _applicationLifecycle = host.Services.GetRequiredService<AvaloniaApplicationLifecycle>();
        _applicationLifecycle.AttachHost(host);

        var desktopContext = host.Services.GetRequiredService<AvaloniaDesktopContext>();
        desktopContext.AttachLifetime(desktop);
        var imageLoader = host.Services.GetRequiredService<IAsyncImageLoader>();
        ImageLoader.AsyncImageLoader = imageLoader;
        ImageBrushLoader.AsyncImageLoader = imageLoader;

        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        desktopContext.AttachMainWindow(mainWindow);
        desktop.MainWindow = mainWindow;

        ThemeHelper.SetTheme(host.Services.GetRequiredService<ISettingsStore>().Current.Basic.ThemeMode);
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

#if !DEBUG
                        _singleInstanceGuard?.Dispose();
                        _singleInstanceGuard = null;
#endif
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
        if (_applicationLifecycle != null)
        {
            ObserveBackgroundTask(_applicationLifecycle.StartHostAsync(), "Application Host startup failed.");
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
