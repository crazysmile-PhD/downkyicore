using System.ComponentModel;
using System.Diagnostics;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.Aria2cNet.Server;

internal sealed class AriaProcessSupervisor
{
    private const int KillWaitMilliseconds = 5000;
    private readonly Lock _sync = new();
    private readonly ILogger<AriaProcessSupervisor> _logger;
    private Process? _process;
    private WindowsProcessJob? _windowsProcessJob;

    public AriaProcessSupervisor(ILogger<AriaProcessSupervisor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

    public bool HasRunningProcess
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        WindowsProcessJob? staleProcessJob;
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("An aria2 process is already being supervised.");
            }

            staleProcessJob = _windowsProcessJob;
            _windowsProcessJob = null;
            _process = process;
        }

        staleProcessJob?.Dispose();
    }

    public void BindToParentLifetime(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var processJob = WindowsProcessJob.TryCreateAndAssign(process, _logger);
        lock (_sync)
        {
            if (!ReferenceEquals(_process, process))
            {
                processJob?.Dispose();
                throw new InvalidOperationException("Only the tracked aria2 process can be bound to the App lifetime.");
            }

            _windowsProcessJob?.Dispose();
            _windowsProcessJob = processJob;
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
            return Kill("aria2c did not exit before timeout.");
        }
    }

    public bool Kill(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process == null)
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                _logger.LogErrorMessage(reason, new TimeoutException(reason));
                process.Kill(entireProcessTree: true);
            }

            if (!process.WaitForExit(KillWaitMilliseconds))
            {
                _logger.LogErrorMessage(
                    "aria2 process did not exit after it was terminated.",
                    new TimeoutException("aria2 process termination timed out."));
                return false;
            }

            Release(process);
            return true;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogErrorMessage("aria2 process cleanup failed because its state changed.", e);
            return false;
        }
        catch (Win32Exception e)
        {
            _logger.LogErrorMessage("aria2 process cleanup failed at the operating-system boundary.", e);
            return false;
        }
    }

    internal void SetTrackedProcessForTests(Process? process)
    {
        lock (_sync)
        {
            _windowsProcessJob?.Dispose();
            _windowsProcessJob = null;
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
        WindowsProcessJob? processJob = null;
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                processJob = _windowsProcessJob;
                _windowsProcessJob = null;
            }
        }

        processJob?.Dispose();
    }
}
