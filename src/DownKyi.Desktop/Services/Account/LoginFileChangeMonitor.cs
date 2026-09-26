using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Storage;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Services.Account;

internal sealed class LoginFileChangeMonitor : IHostedService, IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action _invalidateCache;

    public LoginFileChangeMonitor()
        : this(ApplicationStorage.GetLogin(), LoginHelper.InvalidateLoginInfoCache)
    {
    }

    internal LoginFileChangeMonitor(string loginPath, Action invalidateCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginPath);
        _invalidateCache = invalidateCache ?? throw new ArgumentNullException(nameof(invalidateCache));

        var fullPath = Path.GetFullPath(loginPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("The login path has no parent directory.", nameof(loginPath));
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnLoginFileChanged;
        _watcher.Created += OnLoginFileChanged;
        _watcher.Deleted += OnLoginFileChanged;
        _watcher.Renamed += OnLoginFileRenamed;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _watcher.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }

    private void OnLoginFileChanged(object sender, FileSystemEventArgs e)
    {
        _invalidateCache();
    }

    private void OnLoginFileRenamed(object sender, RenamedEventArgs e)
    {
        _invalidateCache();
    }
}
