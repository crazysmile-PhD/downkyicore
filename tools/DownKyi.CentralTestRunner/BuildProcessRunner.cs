using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace DownKyi.CentralTestRunner;

internal static class BuildProcessRunner
{
    internal static async Task<int> BuildProjectAsync(
        string projectPath,
        string configuration,
        bool noRestore,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateBuildStartInfo(projectPath, configuration, noRestore);
        var cleanupResourceDirectory = OperatingSystem.IsWindows()
            ? Path.GetDirectoryName(Path.GetFullPath(projectPath))
            : null;

        return await RunAsync(
            startInfo,
            cancellationToken,
            cleanupResourceDirectory: cleanupResourceDirectory).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateBuildStartInfo(
        string projectPath,
        string configuration,
        bool noRestore)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        if (noRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        return startInfo;
    }

    internal static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null,
        string? cleanupResourceDirectory = null)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupWindow = cleanupTimeout ?? TimeSpan.FromSeconds(5);
            await CleanupAfterCancellationAsync(
                process,
                cleanupWindow,
                cleanupResourceDirectory: cleanupResourceDirectory).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic failure must be retained while mandatory process termination still runs.")]
    internal static async Task CleanupAfterCancellationAsync(
        Process process,
        TimeSpan cleanupWindow,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>>? captureSnapshotAsync = null,
        string? cleanupResourceDirectory = null)
    {
        var captureSnapshot = captureSnapshotAsync ?? ProcessTreeSnapshot.CaptureAsync;
        FinalProcessSnapshot? ownedProcesses = null;
        ExceptionDispatchInfo? snapshotFailure = null;
        try
        {
            ownedProcesses = await captureSnapshot(process.Id, cleanupWindow).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            snapshotFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            KillOwnedProcessTree(process);
            await WaitForRootExitAsync(process, cleanupWindow).ConfigureAwait(false);
            if (ownedProcesses is not null)
            {
                await WaitForOwnedProcessesToExitAsync(ownedProcesses.Processes, cleanupWindow)
                    .ConfigureAwait(false);
            }

            if (OperatingSystem.IsWindows() && cleanupResourceDirectory is not null)
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    cleanupResourceDirectory,
                    cleanupWindow).ConfigureAwait(false);
            }
        }
        catch (Exception cleanupFailure) when (snapshotFailure is not null)
        {
            throw new AggregateException(
                $"The process snapshot failed: {snapshotFailure.SourceException.Message} " +
                $"Mandatory process cleanup also failed: {cleanupFailure.Message}",
                snapshotFailure.SourceException,
                cleanupFailure);
        }

        snapshotFailure?.Throw();
    }

    internal static void KillOwnedProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            (exception is InvalidOperationException or System.ComponentModel.Win32Exception) &&
            process.HasExited)
        {
            // The build exited between the liveness check and the kill request.
        }
    }

    internal static async Task WaitForRootExitAsync(Process process, TimeSpan cleanupTimeout)
    {
        await process.WaitForExitAsync()
            .WaitAsync(cleanupTimeout)
            .ConfigureAwait(false);
    }

    internal static async Task WaitForOwnedProcessesToExitAsync(
        IReadOnlyList<ObservedProcess> ownedProcesses,
        TimeSpan cleanupTimeout,
        Func<Process, DateTimeOffset>? readStartTimeUtc = null)
    {
        var waits = ownedProcesses.Select(
            observedProcess => WaitForObservedProcessExitAsync(
                observedProcess,
                readStartTimeUtc ?? ReadStartTimeUtc));
        await Task.WhenAll(waits).WaitAsync(cleanupTimeout).ConfigureAwait(false);
    }

    internal static async Task WaitForObservedProcessExitAsync(
        ObservedProcess observedProcess,
        Func<Process, DateTimeOffset>? readStartTimeUtc = null,
        Func<Process, bool>? readHasExited = null,
        Func<int, bool>? isProcessPresent = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(observedProcess.Pid);
        }
        catch (ArgumentException)
        {
            // The observed process exited before its identity could be opened.
            return;
        }

        using (process)
        {
            try
            {
                if (observedProcess.StartTimeUtc is { } expectedStartTime &&
                    (readStartTimeUtc ?? ReadStartTimeUtc)(process) != expectedStartTime)
                {
                    return;
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsProcessObservationFailure(exception))
            {
                if (!HasExitedAfterIdentityFailure(
                        process,
                        observedProcess.Pid,
                        readHasExited ?? ReadHasExited,
                        isProcessPresent ?? IsProcessPresent))
                {
                    throw;
                }

                // The observed process exited between opening it and reading or waiting on its identity.
            }
        }
    }

    private static bool HasExitedAfterIdentityFailure(
        Process process,
        int processId,
        Func<Process, bool> readHasExited,
        Func<int, bool> isProcessPresent)
    {
        try
        {
            if (readHasExited(process))
            {
                return true;
            }
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception))
        {
            // A vanished process can make both StartTime and HasExited unavailable.
        }

        try
        {
            return !isProcessPresent(processId);
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception))
        {
            // An inconclusive secondary observation must not replace the first identity failure.
            return false;
        }
    }

    private static bool IsProcessObservationFailure(Exception exception)
    {
        return exception is InvalidOperationException or System.ComponentModel.Win32Exception;
    }

    private static bool ReadHasExited(Process process)
    {
        return process.HasExited;
    }

    private static bool IsProcessPresent(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static DateTimeOffset ReadStartTimeUtc(Process process)
    {
        return process.StartTime.ToUniversalTime();
    }
}
