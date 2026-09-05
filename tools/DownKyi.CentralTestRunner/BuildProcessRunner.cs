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
            cleanupResourceDirectory: cleanupResourceDirectory,
            diagnosticWorkingDirectory: Path.GetDirectoryName(Path.GetFullPath(projectPath))).ConfigureAwait(false);
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
        string? cleanupResourceDirectory = null,
        string? diagnosticWorkingDirectory = null)
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
                cleanupResourceDirectory: cleanupResourceDirectory,
                cancellationRequested: cancellationToken.IsCancellationRequested,
                diagnosticWorkingDirectory: diagnosticWorkingDirectory ?? startInfo.WorkingDirectory)
                .ConfigureAwait(false);
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
        string? cleanupResourceDirectory = null,
        bool cancellationRequested = false,
        string? diagnosticWorkingDirectory = null)
    {
        var captureSnapshot = captureSnapshotAsync ?? ProcessTreeSnapshot.CaptureAsync;
        var diagnostic = new CancellationCleanupDiagnostic(
            process.Id,
            cancellationRequested,
            diagnosticWorkingDirectory ?? Directory.GetCurrentDirectory());
        diagnostic.Record("cancellation-observed", process.Id);
        FinalProcessSnapshot? ownedProcesses = null;
        ExceptionDispatchInfo? snapshotFailure = null;
        diagnostic.Record("process-relationship-snapshot-start", process.Id);
        try
        {
            ownedProcesses = await captureSnapshot(process.Id, cleanupWindow).ConfigureAwait(false);
            diagnostic.Record(
                "process-relationship-snapshot-success",
                process.Id,
                $"observed={ownedProcesses.Processes.Count}");
            foreach (var observedProcess in ownedProcesses.Processes)
            {
                diagnostic.Record(
                    "snapshot-process-observed",
                    observedProcess.Pid,
                    $"parent={observedProcess.ParentPid} identity={(observedProcess.StartTimeUtc is null ? "missing" : "present")}");
            }
        }
        catch (Exception exception)
        {
            diagnostic.Record(
                "process-relationship-snapshot-failure",
                process.Id,
                $"exception={exception.GetType().FullName}");
            snapshotFailure = ExceptionDispatchInfo.Capture(exception);
        }

        var cleanupStage = "kill-owned-process-tree";
        try
        {
            diagnostic.Record("kill-owned-process-tree-start", process.Id);
            KillOwnedProcessTree(process);
            diagnostic.Record("kill-owned-process-tree-success", process.Id);
            cleanupStage = "root-reap";
            diagnostic.Record("root-reap-start", process.Id);
            await WaitForRootExitAsync(process, cleanupWindow).ConfigureAwait(false);
            diagnostic.Record("root-reap-success", process.Id);
            if (ownedProcesses is not null)
            {
                cleanupStage = "observed-descendant-reap";
                diagnostic.Record(
                    "observed-descendant-reap-start",
                    process.Id,
                    $"observed={ownedProcesses.Processes.Count}");
                await WaitForOwnedProcessesToExitAsync(
                        ownedProcesses.Processes,
                        cleanupWindow,
                        diagnostic: diagnostic)
                    .ConfigureAwait(false);
                diagnostic.Record("observed-descendant-reap-success", process.Id);
            }

            if (OperatingSystem.IsWindows() && cleanupResourceDirectory is not null)
            {
                cleanupStage = "windows-resource-readiness";
                diagnostic.Record("windows-resource-readiness-start", process.Id);
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    cleanupResourceDirectory,
                    cleanupWindow).ConfigureAwait(false);
                diagnostic.Record("windows-resource-readiness-success", process.Id);
            }
        }
        catch (Exception cleanupFailure)
        {
            diagnostic.Record(
                "unexpected-exception-escaping-cleanup",
                process.Id,
                $"stage={cleanupStage} exception={cleanupFailure.GetType().FullName}");
            var firstFailure = snapshotFailure?.SourceException ?? cleanupFailure;
            var firstFailureStage = snapshotFailure is null
                ? cleanupStage
                : "process-relationship-snapshot";
            if (snapshotFailure is not null)
            {
                var aggregate = new AggregateException(
                    $"The process snapshot failed: {snapshotFailure.SourceException.Message} " +
                    $"Mandatory process cleanup also failed: {cleanupFailure.Message}",
                    snapshotFailure.SourceException,
                    cleanupFailure);
                diagnostic.AttachFailure(aggregate, firstFailureStage, firstFailure, cleanupFailure);
                throw aggregate;
            }

            diagnostic.AttachFailure(cleanupFailure, firstFailureStage, firstFailure, cleanupFailure);
            throw;
        }

        if (snapshotFailure is not null)
        {
            diagnostic.Record(
                "unexpected-exception-escaping-cleanup",
                process.Id,
                "stage=process-relationship-snapshot");
            diagnostic.AttachFailure(
                snapshotFailure.SourceException,
                "process-relationship-snapshot",
                snapshotFailure.SourceException,
                snapshotFailure.SourceException);
            snapshotFailure.Throw();
        }

        diagnostic.Record("original-cancellation-rethrow", process.Id);
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
        Func<Process, DateTimeOffset>? readStartTimeUtc = null,
        CancellationCleanupDiagnostic? diagnostic = null)
    {
        var waits = ownedProcesses.Select(
            observedProcess => WaitForObservedProcessExitAsync(
                observedProcess,
                readStartTimeUtc ?? ReadStartTimeUtc,
                diagnostic: diagnostic));
        await Task.WhenAll(waits).WaitAsync(cleanupTimeout).ConfigureAwait(false);
    }

    internal static async Task WaitForObservedProcessExitAsync(
        ObservedProcess observedProcess,
        Func<Process, DateTimeOffset>? readStartTimeUtc = null,
        Func<Process, bool>? readHasExited = null,
        Func<int, bool>? isProcessPresent = null,
        CancellationCleanupDiagnostic? diagnostic = null)
    {
        var markers = new ObservedProcessModuleMarkers(observedProcess.Pid);
        Process process;
        markers.Enter(ObservedProcessModule.GetProcessById);
        try
        {
            process = Process.GetProcessById(observedProcess.Pid);
            markers.Complete(ObservedProcessModule.GetProcessById);
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
                markers.Enter(ObservedProcessModule.IdentityRead);
                var identityMatches = observedProcess.StartTimeUtc is not { } expectedStartTime ||
                    (readStartTimeUtc ?? ReadStartTimeUtc)(process) == expectedStartTime;
                markers.Complete(ObservedProcessModule.IdentityRead);
                if (!identityMatches)
                {
                    return;
                }

                markers.Enter(ObservedProcessModule.WaitForExit);
                await process.WaitForExitAsync().ConfigureAwait(false);
                markers.Complete(ObservedProcessModule.WaitForExit);
            }
            catch (Exception exception) when (IsProcessObservationFailure(exception))
            {
                if (!HasExitedAfterIdentityFailure(
                        process,
                        observedProcess.Pid,
                        readHasExited ?? ReadHasExited,
                        isProcessPresent ?? IsProcessPresent,
                        ref markers))
                {
                    diagnostic?.RecordObservedProcessFailure(in markers, exception);
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
        Func<int, bool> isProcessPresent,
        ref ObservedProcessModuleMarkers markers)
    {
        markers.Enter(ObservedProcessModule.HasExitedFallback);
        try
        {
            var hasExited = readHasExited(process);
            markers.ObserveHasExited(hasExited);
            markers.Complete(ObservedProcessModule.HasExitedFallback);
            if (hasExited)
            {
                return true;
            }
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception))
        {
            // A vanished process can make both StartTime and HasExited unavailable.
        }

        markers.Enter(ObservedProcessModule.PidPresenceFallback);
        try
        {
            var present = isProcessPresent(processId);
            markers.ObservePidPresence(present);
            markers.Complete(ObservedProcessModule.PidPresenceFallback);
            return !present;
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
