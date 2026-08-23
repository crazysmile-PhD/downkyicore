using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal interface IProcessRestartLauncher
{
    IProcessRestartTransaction? TryPrepareHelper(int parentProcessId);
}

internal interface IProcessRestartTransaction
{
    Task CommitAsync();

    Task RevokeAsync();
}

internal sealed class ProcessRestartLauncher(ILogger<ProcessRestartLauncher> logger) : IProcessRestartLauncher
{
    internal const string WaitForParentArgument = "--restart-after-pid";

    private readonly ILogger<ProcessRestartLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IProcessRestartTransaction? TryPrepareHelper(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        try
        {
            var process = Process.Start(CreateStartInfo(parentProcessId));
            if (process != null)
            {
                return new ProcessRestartTransaction(process);
            }

            _logger.LogWarningMessage("The restart helper could not be started.");
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            _logger.LogErrorMessage("The restart helper could not be started.", e);
            return null;
        }
    }

    public static async Task<bool> RunHelperIfRequestedAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParseParentProcessId(arguments, out var parentProcessId))
        {
            return false;
        }

        await WaitForParentExitAsync(parentProcessId, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(CreateStartInfo(null));
        if (process == null)
        {
            throw new InvalidOperationException("The application could not be relaunched.");
        }

        return true;
    }

    private static async Task WaitForParentExitAsync(
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return;
        }
    }

    internal static bool TryParseParentProcessId(
        IReadOnlyList<string> arguments,
        out int parentProcessId)
    {
        parentProcessId = 0;
        return arguments.Count == 2
               && string.Equals(arguments[0], WaitForParentArgument, StringComparison.Ordinal)
               && int.TryParse(
                   arguments[1],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out parentProcessId)
               && parentProcessId > 0;
    }

    internal static ProcessStartInfo CreateStartInfo(int? parentProcessId)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (isDotnetHost)
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                throw new InvalidOperationException("The managed application entry point is unavailable.");
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        if (parentProcessId is { } processId)
        {
            startInfo.ArgumentList.Add(WaitForParentArgument);
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    private sealed class ProcessRestartTransaction(Process process) : IProcessRestartTransaction
    {
        private readonly Process _process = process;
        private int _completionState;

        public Task CommitAsync()
        {
            EnsureOwnsHelper();
            _completionState = 1;
            _process.Dispose();
            return Task.CompletedTask;
        }

        public async Task RevokeAsync()
        {
            EnsureOwnsHelper();
            _completionState = 2;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private void EnsureOwnsHelper()
        {
            if (_completionState != 0)
            {
                throw new InvalidOperationException(
                    "The restart helper transaction has already completed.");
            }
        }
    }
}
