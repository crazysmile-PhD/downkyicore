using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Account;

internal sealed class LoginFileChangeMonitor : IHostedService, IDisposable
{
    private readonly Func<FileSystemWatcher> _watcherFactory;
    private readonly Action _invalidateCache;
    private readonly ILogger<LoginFileChangeMonitor> _logger;
    private FileSystemWatcher? _watcher;
    private bool _hotRefreshDisabled;

    public LoginFileChangeMonitor(ILogger<LoginFileChangeMonitor> logger)
        : this(ApplicationStorage.GetLogin(), LoginHelper.InvalidateLoginInfoCache, logger)
    {
    }

    internal LoginFileChangeMonitor(
        string loginPath,
        Action invalidateCache,
        ILogger<LoginFileChangeMonitor> logger,
        Func<FileSystemWatcher>? watcherFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginPath);
        _invalidateCache = invalidateCache ?? throw new ArgumentNullException(nameof(invalidateCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _watcherFactory = watcherFactory ?? (() => CreateWatcher(loginPath));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hotRefreshDisabled || _watcher != null)
        {
            return Task.CompletedTask;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = _watcherFactory();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (IsWatcherStartupFailure(e))
        {
            watcher?.Dispose();
            _hotRefreshDisabled = true;
            _logger.LogErrorMessage(
                "Login file monitoring could not be started; external Cookie changes will not refresh automatically.",
                e);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    private void OnLoginFileChanged(object sender, FileSystemEventArgs e)
    {
        _invalidateCache();
    }

    private void OnLoginFileRenamed(object sender, RenamedEventArgs e)
    {
        _invalidateCache();
    }

    internal void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _invalidateCache();
    }

    private FileSystemWatcher CreateWatcher(string loginPath)
    {
        var fullPath = Path.GetFullPath(loginPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("The login path has no parent directory.", nameof(loginPath));
        var watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnLoginFileChanged;
        watcher.Created += OnLoginFileChanged;
        watcher.Deleted += OnLoginFileChanged;
        watcher.Renamed += OnLoginFileRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private static bool IsWatcherStartupFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }
}
