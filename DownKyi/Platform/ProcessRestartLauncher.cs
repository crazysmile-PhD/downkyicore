using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.Platform;

internal interface IProcessRestartLauncher
{
    bool TryStartHelper(int parentProcessId);
}

internal sealed class ProcessRestartLauncher(ILogger<ProcessRestartLauncher> logger) : IProcessRestartLauncher
{
    internal const string WaitForParentArgument = "--restart-after-pid";

    private readonly ILogger<ProcessRestartLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public bool TryStartHelper(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        try
        {
            using var process = Process.Start(CreateStartInfo(parentProcessId));
            if (process != null)
            {
                return true;
            }

            _logger.LogWarningMessage("The restart helper could not be started.");
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            _logger.LogErrorMessage("The restart helper could not be started.", e);
            return false;
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
}
