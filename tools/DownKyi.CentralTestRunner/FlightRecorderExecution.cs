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
    Func<Process, DateTimeOffset>? RootStartTimeReader = null,
    TextWriter? ErrorDestination = null);

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
        var standardOutput = new TailBuffer(8192);
        var standardError = new TailBuffer(8192);
        var scopeStarted = false;

        try
        {
            using var scope = await OwnedProcessScope.StartAsync(request.StartInfo, request.CleanupTimeout)
                .ConfigureAwait(false);
            scopeStarted = true;
            var process = scope.Host;

            using var outputCapture = new CancellationTokenSource();
            var outputTask = Task.Run(() => BoundedOutputCapture.CaptureAsync(
                process.StandardOutput,
                standardOutput,
                Console.Out,
                recorder.Redactor,
                outputCapture.Token), CancellationToken.None);
            var errorTask = Task.Run(() => BoundedOutputCapture.CaptureAsync(
                process.StandardError,
                standardError,
                request.ErrorDestination ?? Console.Error,
                recorder.Redactor,
                outputCapture.Token), CancellationToken.None);
            var rootPid = scope.RootPid;
            DateTimeOffset rootStartTime;
            try
            {
                rootStartTime = request.RootStartTimeReader?.Invoke(process) ?? scope.RootStartTimeUtc;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var cleanup = new CleanupDeadline(request.CleanupTimeout);
                recorder.RecordInMemory("root_identity_failed", pid: rootPid, detail: exception.Message);
                await recorder.CaptureFinalSnapshotOnceAsync(cleanup).ConfigureAwait(false);
                await StopAsync(scope, cleanup, recorder).ConfigureAwait(false);
                await DrainOutputAsync(
                    outputTask,
                    errorTask,
                    outputCapture,
                    cleanup,
                    recorder,
                    rootPid).ConfigureAwait(false);
                await recorder.FinalizeFailureAsync("root_identity_failed", standardOutput, standardError, cleanup)
                    .ConfigureAwait(false);
                return new ProcessExecutionResult(2, rootPid, default, recorder.EvidencePath, recorder);
            }

            recorder.SetRootIdentity(rootPid, rootStartTime);
            recorder.RecordInMemory(
                "process_start",
                pid: rootPid,
                startTimeUtc: rootStartTime);
            TracePhase(recorder, rootPid, "process_wait_begin");

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
                TracePhase(recorder, rootPid, eventName);
                TracePhase(recorder, rootPid, "snapshot_begin");
                await recorder.CaptureFinalSnapshotOnceAsync(cleanup).ConfigureAwait(false);
                TracePhase(recorder, rootPid, "snapshot_returned");
                var stopped = await StopAsync(scope, cleanup, recorder).ConfigureAwait(false);
                TracePhase(recorder, rootPid, "drain_begin");
                var drained = await DrainOutputAsync(
                    outputTask,
                    errorTask,
                    outputCapture,
                    cleanup,
                    recorder,
                    rootPid).ConfigureAwait(false);
                TracePhase(recorder, rootPid, "drain_returned");
                TracePhase(recorder, rootPid, "report_begin");
                await recorder.FinalizeFailureAsync(eventName, standardOutput, standardError, cleanup)
                    .ConfigureAwait(false);
                TracePhase(recorder, rootPid, "report_returned");
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
            TracePhase(recorder, rootPid, "process_exited");
            var postExitCleanup = new CleanupDeadline(request.CleanupTimeout);
            recorder.RecordInMemory(
                "process_exit",
                pid: rootPid,
                exitCode: processExitCode);
            if (processExitCode != 0)
            {
                await recorder.CaptureFinalSnapshotOnceAsync(postExitCleanup).ConfigureAwait(false);
            }

            TracePhase(recorder, rootPid, "post_exit_drain_begin");
            var outputDrain = Task.WhenAll(outputTask, errorTask);
            var completed = await Task.WhenAny(
                outputDrain,
                Task.Delay(postExitCleanup.PostExitDrainWindow, CancellationToken.None)).ConfigureAwait(false);
            var outputHeld = completed != outputDrain;

            var scopeStopped = true;
            if (outputHeld)
            {
                recorder.RecordInMemory("post_exit_output_held", pid: rootPid);
                scopeStopped = await StopAsync(scope, postExitCleanup, recorder).ConfigureAwait(false);
            }

            var streamsDrained = await DrainOutputAsync(
                outputTask,
                errorTask,
                outputCapture,
                postExitCleanup,
                recorder,
                rootPid).ConfigureAwait(false);
            TracePhase(recorder, rootPid, "post_exit_drain_returned");
            if (!outputHeld)
            {
                scopeStopped = await StopAsync(
                    scope, postExitCleanup, recorder, "scope_release_requested").ConfigureAwait(false);
            }

            if (outputHeld || !streamsDrained || !scopeStopped)
            {
                await recorder.CaptureFinalSnapshotOnceAsync(postExitCleanup).ConfigureAwait(false);
                var outcome = outputHeld || !streamsDrained ? "stream_drain_failed" : "cleanup_failed";
                await recorder.FinalizeFailureAsync(outcome, standardOutput, standardError, postExitCleanup)
                    .ConfigureAwait(false);
                processExitCode = 2;
            }
            else if (processExitCode != 0)
            {
                recorder.RecordInMemory(
                    "cleanup_completed",
                    pid: rootPid,
                    detail: "natural process exit and stream drain observed");
                await recorder.FinalizeFailureAsync("process_exit", standardOutput, standardError, postExitCleanup)
                    .ConfigureAwait(false);
            }
            else
            {
                recorder.SetOutputTails(standardOutput.Value, standardError.Value);
                recorder.RecordInMemory("cleanup_completed", pid: rootPid, detail: "natural process exit observed");
            }

            return new ProcessExecutionResult(
                processExitCode,
                rootPid,
                rootStartTime,
                recorder.EvidencePath,
                recorder);
        }
        catch (Exception exception) when (!scopeStarted &&
                                          exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException)
        {
            await recorder.RecordAsync("process_start_failed", detail: exception.Message).ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("start_failed", standardOutput, standardError).ConfigureAwait(false);
            return new ProcessExecutionResult(2, 0, default, recorder.EvidencePath, recorder);
        }
    }

    private static void TracePhase(FlightRecorder recorder, int rootPid, string phase)
    {
        recorder.RecordInMemory("phase", pid: rootPid, detail: phase);
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
        OwnedProcessScope scope,
        CleanupDeadline deadline,
        FlightRecorder recorder,
        string requestEvent = "bounded_stop_requested")
    {
        var process = scope.Host;
        try
        {
            TracePhase(recorder, scope.RootPid, "terminate_begin");
            recorder.RecordInMemory(requestEvent, pid: scope.RootPid);
            await scope.TerminateAsync(deadline).ConfigureAwait(false);
            TracePhase(recorder, scope.RootPid, "terminate_returned");
            TracePhase(recorder, scope.RootPid, "root_wait_begin");
            await process.WaitForExitAsync().WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
            TracePhase(recorder, scope.RootPid, "wait_returned");

            recorder.RecordInMemory(
                "process_exit",
                pid: scope.RootPid,
                exitCode: process.ExitCode);
            recorder.RecordInMemory("cleanup_completed", pid: scope.RootPid);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
        {
            TracePhase(recorder, scope.RootPid, "cleanup_failed");
            recorder.RecordInMemory(
                "cleanup_failed",
                pid: scope.RootPid,
                detail: exception.Message);
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
            await drain.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            try
            {
                await outputCapture.CancelAsync().WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Disposing the process also closes its redirected streams.
            }
            recorder.RecordInMemory(
                "cleanup_failed",
                pid: rootPid,
                detail: "stdout/stderr drain exceeded the bounded cleanup window");
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            recorder.RecordInMemory(
                "cleanup_failed",
                pid: rootPid,
                detail: $"stdout/stderr drain failed: {exception.Message}");
            return false;
        }
    }
}
