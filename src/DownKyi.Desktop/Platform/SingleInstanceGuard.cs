using System;
using System.Threading;

namespace DownKyi.Platform;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly RegisteredWaitHandle? _activationRegistration;
    private readonly object _activationSync = new();
    private Action? _activationHandler;
    private bool _activationPending;
    private bool _disposed;

    internal bool HasPendingActivation
    {
        get
        {
            lock (_activationSync)
            {
                return _activationPending;
            }
        }
    }

    private SingleInstanceGuard(
        Mutex mutex,
        string owner,
        string repository)
    {
        _mutex = mutex;
        if (OperatingSystem.IsWindows())
        {
            _activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                BuildActivationEventName(owner, repository),
                new NamedWaitHandleOptions
                {
                    CurrentUserOnly = false,
                    CurrentSessionOnly = false
                },
                out _);
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                static (state, _) => ((SingleInstanceGuard)state!).OnActivationRequested(),
                this,
                Timeout.InfiniteTimeSpan,
                executeOnlyOnce: false);
        }
    }

    public static bool TryAcquire(
        string owner,
        string repository,
        out SingleInstanceGuard? guard)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(
                initiallyOwned: false,
                BuildMutexName(owner, repository),
                new NamedWaitHandleOptions { CurrentUserOnly = false, CurrentSessionOnly = false },
                out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                mutex = null;
                SignalExistingInstance(owner, repository);
                guard = null;
                return false;
            }

            guard = new SingleInstanceGuard(mutex, owner, repository);
            mutex = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An existing global mutex may deny access to another Windows user.
            guard = null;
            return false;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    internal static string BuildMutexName(string owner, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        return $"DownKyi-{owner}-{repository}";
    }

    internal static string BuildActivationEventName(string owner, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        return $"DownKyi-Activate-{owner}-{repository}";
    }

    internal void SetActivationHandler(Action? handler)
    {
        var invokePending = false;
        lock (_activationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activationHandler = handler;
            if (handler != null && _activationPending)
            {
                _activationPending = false;
                invokePending = true;
            }
        }

        if (invokePending)
        {
            handler!();
        }
    }

    private static void SignalExistingInstance(string owner, string repository)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                BuildActivationEventName(owner, repository),
                new NamedWaitHandleOptions
                {
                    CurrentUserOnly = false,
                    CurrentSessionOnly = false
                },
                out _);
            activationEvent.Set();
        }
        catch (UnauthorizedAccessException)
        {
            // The primary process can still retain ownership when cross-user signaling is denied.
            return;
        }
    }

    private void OnActivationRequested()
    {
        Action? handler;
        lock (_activationSync)
        {
            if (_disposed)
            {
                return;
            }

            handler = _activationHandler;
            if (handler == null)
            {
                _activationPending = true;
                return;
            }
        }

        handler();
    }

    public void Dispose()
    {
        lock (_activationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activationHandler = null;
            _activationPending = false;
        }

        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _mutex.Dispose();
    }
}
