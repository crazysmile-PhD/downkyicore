using System;
using System.Threading;

namespace DownKyi.Platform;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(
        string owner,
        string repository,
        out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(
            initiallyOwned: true,
            BuildMutexName(owner, repository),
            new NamedWaitHandleOptions { CurrentUserOnly = false, CurrentSessionOnly = false },
            out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    internal static string BuildMutexName(string owner, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        return $"DownKyi-{owner}-{repository}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseMutexBestEffort();
        _mutex.Dispose();
    }

    private void ReleaseMutexBestEffort()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            return;
        }
    }
}
