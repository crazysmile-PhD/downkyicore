using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal sealed class AvaloniaApplicationLifecycle : IApplicationLifecycle
{
    internal static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogFlushTimeout = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly AvaloniaDesktopContext _desktopContext;
    private readonly IProcessRestartLauncher _restartLauncher;
    private readonly ISettingsStore _settingsStore;
    private readonly IApplicationLogService _logService;
    private readonly ILogger<AvaloniaApplicationLifecycle> _logger;
    private readonly TimeSpan _cleanupTimeout;
    private readonly Func<Task> _desktopShutdown;
    private IHost? _host;
    private Task? _hostStartupTask;
    private Task? _shutdownTask;

    public AvaloniaApplicationLifecycle(
        AvaloniaDesktopContext desktopContext,
        IProcessRestartLauncher restartLauncher,
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        ILogger<AvaloniaApplicationLifecycle> logger)
        : this(
            desktopContext,
            restartLauncher,
            settingsStore,
            logService,
            logger,
            DefaultCleanupTimeout)
    {
    }

    internal AvaloniaApplicationLifecycle(
        AvaloniaDesktopContext desktopContext,
        IProcessRestartLauncher restartLauncher,
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        ILogger<AvaloniaApplicationLifecycle> logger,
        TimeSpan cleanupTimeout,
        Func<Task>? desktopShutdown = null)
    {
        _desktopContext = desktopContext ?? throw new ArgumentNullException(nameof(desktopContext));
        _restartLauncher = restartLauncher ?? throw new ArgumentNullException(nameof(restartLauncher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupTimeout, TimeSpan.Zero);
        _cleanupTimeout = cleanupTimeout;
        _desktopShutdown = desktopShutdown ?? _desktopContext.ShutdownAsync;
    }

    public CancellationToken ShutdownToken => GetHost()
        .Services
        .GetRequiredService<ApplicationCancellation>()
        .ShutdownToken;

    public void AttachHost(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_sync)
        {
            if (_host != null && !ReferenceEquals(_host, host))
            {
                throw new InvalidOperationException("A different application Host is already attached.");
            }

            _host = host;
        }
    }

    public Task StartHostAsync()
    {
        lock (_sync)
        {
            return _hostStartupTask ??= StartHostCoreAsync(GetHost());
        }
    }

    public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return _shutdownTask ??= ShutdownCoreAsync(GetHost());
        }
    }

    public async Task ExitAsync(CancellationToken cancellationToken = default)
    {
        TimeoutException? cleanupTimeoutException = null;
        try
        {
            await RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            cleanupTimeoutException = e;
        }

        try
        {
            await _desktopShutdown().ConfigureAwait(false);
        }
        catch (Exception desktopShutdownException) when (cleanupTimeoutException != null)
        {
            throw new AggregateException(
                "Application cleanup and desktop shutdown both failed.",
                cleanupTimeoutException,
                desktopShutdownException);
        }

        if (cleanupTimeoutException != null)
        {
            ExceptionDispatchInfo.Capture(cleanupTimeoutException).Throw();
        }
    }

    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_restartLauncher.TryStartHelper(Environment.ProcessId))
        {
            return false;
        }

        await ExitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task StartHostCoreAsync(IHost host)
    {
        try
        {
            await host.StartAsync(ShutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogErrorMessage("Application Host startup failed.", e);
        }
    }

    private async Task ShutdownCoreAsync(IHost host)
    {
        await host.Services
            .GetRequiredService<ApplicationCancellation>()
            .RequestShutdownAsync()
            .ConfigureAwait(false);

        var cleanupTasks = new List<Task>
        {
            host.StopAsync(CancellationToken.None),
            _settingsStore.FlushAsync(CancellationToken.None)
        };
        Task? startupTask;
        lock (_sync)
        {
            startupTask = _hostStartupTask;
        }

        if (startupTask != null)
        {
            cleanupTasks.Add(startupTask);
        }

        var cleanup = Task.WhenAll(cleanupTasks);
        TimeoutException? cleanupTimeoutException = null;
        if (await Task.WhenAny(cleanup, Task.Delay(_cleanupTimeout)).ConfigureAwait(false) == cleanup)
        {
            try
            {
                await cleanup.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                _logger.LogDebugMessage("Application cleanup was canceled by shutdown.");
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
                or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                or System.ComponentModel.Win32Exception)
            {
                _logger.LogErrorMessage("Application cleanup failed during shutdown.", e);
            }
        }
        else
        {
            _logger.LogWarningMessage("Application cleanup timed out; killing the tracked aria2 process.");
            host.Services
                .GetService<AriaServer>()?
                .KillTrackedServer("application exit cleanup timed out.");
            _ = cleanup.ContinueWith(
                task => _logger.LogErrorMessage(
                    "Application cleanup failed after the shutdown timeout.",
                    task.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            cleanupTimeoutException = new TimeoutException(
                $"Application cleanup did not complete within {_cleanupTimeout}.");
        }

        using var flushCancellation = new CancellationTokenSource(LogFlushTimeout);
        try
        {
            await _logService.FlushAsync(flushCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flushCancellation.IsCancellationRequested)
        {
            _logger.LogWarningMessage("Application log flush timed out during shutdown.");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _logger.LogErrorMessage("Application log flush failed during shutdown.", e);
        }

        if (cleanupTimeoutException != null)
        {
            throw cleanupTimeoutException;
        }
    }

    private IHost GetHost()
    {
        lock (_sync)
        {
            return _host
                ?? throw new InvalidOperationException("The application Host has not been attached.");
        }
    }
}
