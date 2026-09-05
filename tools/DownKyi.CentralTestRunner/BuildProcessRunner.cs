using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal static class BuildProcessRunner
{
    internal static async Task<int> BuildProjectAsync(
        string projectPath,
        string configuration,
        bool noRestore,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        if (noRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        return await RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null)
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
            var ownedProcesses = await ProcessTreeSnapshot.CaptureAsync(process.Id, cleanupWindow)
                .ConfigureAwait(false);
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

            await process.WaitForExitAsync()
                .WaitAsync(cleanupWindow)
                .ConfigureAwait(false);
            await WaitForOwnedProcessesToExitAsync(ownedProcesses.Processes, cleanupWindow)
                .ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static async Task WaitForOwnedProcessesToExitAsync(
        IReadOnlyList<ObservedProcess> ownedProcesses,
        TimeSpan cleanupTimeout)
    {
        var waits = ownedProcesses.Select(WaitForObservedProcessExitAsync);
        await Task.WhenAll(waits).WaitAsync(cleanupTimeout).ConfigureAwait(false);
    }

    private static async Task WaitForObservedProcessExitAsync(ObservedProcess observedProcess)
    {
        try
        {
            using var process = Process.GetProcessById(observedProcess.Pid);
            if (observedProcess.StartTimeUtc is { } expectedStartTime &&
                process.StartTime.ToUniversalTime() != expectedStartTime)
            {
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The observed process exited before its identity could be opened.
        }
    }
}
