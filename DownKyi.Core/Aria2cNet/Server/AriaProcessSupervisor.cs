using System.ComponentModel;
using System.Diagnostics;
using DownKyi.Core.Logging;

namespace DownKyi.Core.Aria2cNet.Server;

internal sealed class AriaProcessSupervisor
{
    private const string Tag = nameof(AriaProcessSupervisor);
    private readonly Lock _sync = new();
    private Process? _process;

    public bool HasTrackedProcess
    {
        get
        {
            lock (_sync)
            {
                return _process != null;
            }
        }
    }

    public void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("An aria2 process is already being supervised.");
            }

            _process = process;
        }
    }

    public async Task<bool> WaitForExitOrKillAsync(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var process = GetTrackedProcess();
        if (process == null)
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            }

            Release(process);
            return true;
        }
        catch (TimeoutException)
        {
            Kill("aria2c did not exit before timeout.");
            return false;
        }
    }

    public bool Kill(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process == null)
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                LogManager.Error(Tag, new TimeoutException(reason));
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (InvalidOperationException e)
        {
            LogManager.Error(Tag, e);
            return false;
        }
        catch (Win32Exception e)
        {
            LogManager.Error(Tag, e);
            return false;
        }
    }

    internal void SetTrackedProcessForTests(Process? process)
    {
        lock (_sync)
        {
            _process = process;
        }
    }

    private Process? GetTrackedProcess()
    {
        lock (_sync)
        {
            return _process;
        }
    }

    private void Release(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }
    }
}
