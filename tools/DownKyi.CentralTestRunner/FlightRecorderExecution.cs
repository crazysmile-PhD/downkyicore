using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal sealed record ProcessExecutionRequest(
    string SliceIdentity,
    string TestIdentity,
    ProcessStartInfo StartInfo,
    TimeSpan Timeout,
    TimeSpan CleanupTimeout,
    string EvidenceDirectory,
    Func<int, TimeSpan, Task<FinalProcessSnapshot>>? SnapshotCapture = null,
    Func<Process, DateTimeOffset>? RootStartTimeReader = null);

internal sealed record ProcessExecutionResult(
    int ExitCode,
    int RootPid,
    DateTimeOffset RootStartTimeUtc,
    string EvidencePath,
    FlightRecorder Recorder);

internal static class FlightRecorderExecution
{
    public static async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SliceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EvidenceDirectory);

        var recorder = await FlightRecorder.CreateAsync(request).ConfigureAwait(false);
        using var process = new Process { StartInfo = request.StartInfo };
        var standardOutput = new TailBuffer(8192);
        var standardError = new TailBuffer(8192);

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await recorder.RecordAsync("process_start_failed", detail: exception.Message).ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("start_failed", standardOutput, standardError).ConfigureAwait(false);
            return new ProcessExecutionResult(2, 0, default, recorder.EvidencePath, recorder);
        }

        using var outputCapture = new CancellationTokenSource();
        var outputTask = BoundedOutputCapture.CaptureAsync(
            process.StandardOutput,
            standardOutput,
            Console.Out,
            recorder.Redactor,
            outputCapture.Token);
        var errorTask = BoundedOutputCapture.CaptureAsync(
            process.StandardError,
            standardError,
            Console.Error,
            recorder.Redactor,
            outputCapture.Token);
        var rootPid = process.Id;
        DateTimeOffset rootStartTime;
        try
        {
            rootStartTime = (request.RootStartTimeReader ?? ReadRootStartTimeUtc)(process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var cleanup = new CleanupDeadline(request.CleanupTimeout);
            recorder.RecordInMemory("root_identity_failed", pid: rootPid, detail: exception.Message);
            await recorder.CaptureFinalSnapshotOnceAsync(cleanup).ConfigureAwait(false);
            await StopAsync(process, cleanup, recorder).ConfigureAwait(false);
            await DrainOutputAsync(
                outputTask,
                errorTask,
                outputCapture,
                cleanup,
                recorder,
                rootPid).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("root_identity_failed", standardOutput, standardError)
                .ConfigureAwait(false);
            return new ProcessExecutionResult(2, rootPid, default, recorder.EvidencePath, recorder);
        }

        recorder.SetRootIdentity(rootPid, rootStartTime);
        await recorder.RecordAsync(
            "process_start",
            pid: rootPid,
            startTimeUtc: rootStartTime).ConfigureAwait(false);
        await TraceMacOsPhaseAsync(rootPid, "process_wait_begin").ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cleanup = new CleanupDeadline(request.CleanupTimeout);
            var eventName = cancellationToken.IsCancellationRequested ? "cancellation" : "timeout";
            recorder.RecordInMemory(eventName, pid: rootPid);
            await TraceMacOsPhaseAsync(rootPid, eventName, cleanup).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "snapshot_begin", cleanup).ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync(cleanup).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "snapshot_returned", cleanup).ConfigureAwait(false);
            var stopped = await StopAsync(process, cleanup, recorder).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "drain_begin", cleanup).ConfigureAwait(false);
            var drained = await DrainOutputAsync(
                outputTask,
                errorTask,
                outputCapture,
                cleanup,
                recorder,
                rootPid).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "drain_returned", cleanup).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "report_begin", cleanup).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync(eventName, standardOutput, standardError)
                .ConfigureAwait(false);
            await TraceMacOsPhaseAsync(rootPid, "report_returned", cleanup).ConfigureAwait(false);
            var exitCode = string.Equals(eventName, "timeout", StringComparison.Ordinal)
                ? 124
                : stopped && drained ? 130 : 2;
            return new ProcessExecutionResult(
                exitCode,
                rootPid,
                rootStartTime,
                recorder.EvidencePath,
                recorder);
        }

        var processExitCode = process.ExitCode;
        await TraceMacOsPhaseAsync(rootPid, "process_exited").ConfigureAwait(false);
        var postExitCleanup = new CleanupDeadline(request.CleanupTimeout);
        await recorder.RecordAsync(
            "process_exit",
            pid: rootPid,
            exitCode: processExitCode).ConfigureAwait(false);
        if (processExitCode != 0)
        {
            await recorder.CaptureFinalSnapshotOnceAsync(postExitCleanup).ConfigureAwait(false);
        }

        await TraceMacOsPhaseAsync(rootPid, "post_exit_drain_begin", postExitCleanup).ConfigureAwait(false);
        var streamsDrained = await DrainOutputAsync(
            outputTask,
            errorTask,
            outputCapture,
            postExitCleanup,
            recorder,
            rootPid).ConfigureAwait(false);
        await TraceMacOsPhaseAsync(rootPid, "post_exit_drain_returned", postExitCleanup).ConfigureAwait(false);
        if (!streamsDrained)
        {
            await recorder.CaptureFinalSnapshotOnceAsync(postExitCleanup).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("stream_drain_failed", standardOutput, standardError)
                .ConfigureAwait(false);
            processExitCode = 2;
        }
        else if (processExitCode != 0)
        {
            await recorder.RecordAsync(
                "cleanup_completed",
                pid: rootPid,
                detail: "natural process exit and stream drain observed").ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("process_exit", standardOutput, standardError)
                .ConfigureAwait(false);
        }
        else
        {
            recorder.SetOutputTails(standardOutput.Value, standardError.Value);
            await recorder.RecordAsync("cleanup_completed", pid: rootPid, detail: "natural process exit observed")
                .ConfigureAwait(false);
        }

        return new ProcessExecutionResult(
            processExitCode,
            rootPid,
            rootStartTime,
            recorder.EvidencePath,
            recorder);
    }

    private static DateTimeOffset ReadRootStartTimeUtc(Process process)
    {
        return process.StartTime.ToUniversalTime();
    }

    private static async Task TraceMacOsPhaseAsync(int rootPid, string phase, CleanupDeadline? deadline = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            var maximumWindow = TimeSpan.FromMilliseconds(250);
            var window = deadline is null || deadline.Remaining > maximumWindow
                ? maximumWindow
                : deadline.Remaining;
            if (window == TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await Console.Error.WriteLineAsync($"CentralTestRunner pid={rootPid} phase={phase}")
                    .WaitAsync(window).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
            {
                // Progress logging must not prevent mandatory process cleanup.
            }
        }
    }

    public static async Task PreservePostExitFailureAsync(
        ProcessExecutionResult result,
        string eventName,
        string detail)
    {
        await result.Recorder.RecordAsync(
            eventName,
            pid: result.RootPid,
            detail: detail).ConfigureAwait(false);
        await result.Recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
        await result.Recorder.FinalizeFailureAsync(
            eventName,
            new TailBuffer(1),
            new TailBuffer(1)).ConfigureAwait(false);
    }

    public static Task DiscardAsync(ProcessExecutionResult result)
    {
        File.Delete(result.EvidencePath);
        return Task.CompletedTask;
    }

    private static async Task<bool> StopAsync(
        Process process,
        CleanupDeadline deadline,
        FlightRecorder recorder)
    {
        try
        {
            await TraceMacOsPhaseAsync(process.Id, "terminate_begin", deadline).ConfigureAwait(false);
            var terminationFailures = await OwnedProcessTerminator.TerminateAsync(
                process, recorder.CapturedProcesses, deadline).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(process.Id, "terminate_returned", deadline).ConfigureAwait(false);
            await recorder.RecordAsync("bounded_stop_requested", pid: process.Id).ConfigureAwait(false);
            await TraceMacOsPhaseAsync(process.Id, "root_wait_begin", deadline).ConfigureAwait(false);
            await process.WaitForExitAsync().WaitAsync(deadline.Remaining).ConfigureAwait(false);
            if (recorder.CapturedProcesses is { } captured)
            {
                await TraceMacOsPhaseAsync(process.Id, "descendant_wait_begin", deadline).ConfigureAwait(false);
                await BuildProcessRunner.WaitForOwnedProcessesToExitAsync(
                    captured, deadline.Remaining).ConfigureAwait(false);
            }

            await TraceMacOsPhaseAsync(process.Id, "wait_returned", deadline).ConfigureAwait(false);

            if (terminationFailures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", terminationFailures));
            }

            await recorder.RecordAsync(
                "process_exit",
                pid: process.Id,
                exitCode: process.ExitCode).ConfigureAwait(false);
            await recorder.RecordAsync("cleanup_completed", pid: process.Id).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
        {
            await TraceMacOsPhaseAsync(process.Id, "cleanup_failed", deadline).ConfigureAwait(false);
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: process.Id,
                detail: exception.Message).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> DrainOutputAsync(
        Task outputTask,
        Task errorTask,
        CancellationTokenSource outputCapture,
        CleanupDeadline deadline,
        FlightRecorder recorder,
        int rootPid)
    {
        var drain = Task.WhenAll(outputTask, errorTask);
        try
        {
            await drain.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            try
            {
                await outputCapture.CancelAsync().WaitAsync(deadline.Remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Disposing the process also closes its redirected streams.
            }
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: rootPid,
                detail: "stdout/stderr drain exceeded the bounded cleanup window")
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: rootPid,
                detail: $"stdout/stderr drain failed: {exception.Message}")
                .ConfigureAwait(false);
            return false;
        }
    }
}
