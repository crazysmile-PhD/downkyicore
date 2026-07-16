using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        string installDirectory,
        out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(
            initiallyOwned: true,
            BuildMutexName(owner, repository, installDirectory),
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

    internal static string BuildMutexName(string owner, string repository, string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        var installPath = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installPath)).AsSpan(0, 8));
        var prefix = OperatingSystem.IsWindows() ? @"Global\" : string.Empty;
        return $"{prefix}DownKyi-{owner}-{repository}-{hash}";
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
