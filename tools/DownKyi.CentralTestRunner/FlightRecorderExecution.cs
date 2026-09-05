using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal sealed record ProcessExecutionRequest(
    string SliceIdentity,
    string TestIdentity,
    ProcessStartInfo StartInfo,
    TimeSpan Timeout,
    TimeSpan CleanupTimeout,
    string EvidenceDirectory,
    Func<int, TimeSpan, Task<FinalProcessSnapshot>>? SnapshotCapture = null);

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

        var rootPid = process.Id;
        DateTimeOffset rootStartTime;
        try
        {
            rootStartTime = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await recorder.RecordAsync(
                "root_identity_failed",
                pid: rootPid,
                detail: exception.Message).ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
            await StopAsync(process, request.CleanupTimeout, recorder).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("root_identity_failed", standardOutput, standardError)
                .ConfigureAwait(false);
            return new ProcessExecutionResult(2, rootPid, default, recorder.EvidencePath, recorder);
        }

        recorder.SetRootIdentity(rootPid, rootStartTime);
        await recorder.RecordAsync(
            "process_start",
            pid: rootPid,
            startTimeUtc: rootStartTime).ConfigureAwait(false);

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
            var eventName = cancellationToken.IsCancellationRequested ? "cancellation" : "timeout";
            await recorder.RecordAsync(eventName, pid: rootPid).ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
            await StopAsync(process, request.CleanupTimeout, recorder).ConfigureAwait(false);
            await DrainOutputAsync(
                outputTask,
                errorTask,
                outputCapture,
                request.CleanupTimeout,
                recorder,
                rootPid).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync(eventName, standardOutput, standardError)
                .ConfigureAwait(false);
            var exitCode = string.Equals(eventName, "timeout", StringComparison.Ordinal) ? 124 : 130;
            return new ProcessExecutionResult(
                exitCode,
                rootPid,
                rootStartTime,
                recorder.EvidencePath,
                recorder);
        }

        var processExitCode = process.ExitCode;
        await recorder.RecordAsync(
            "process_exit",
            pid: rootPid,
            exitCode: processExitCode).ConfigureAwait(false);
        if (processExitCode != 0)
        {
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
        }

        var streamsDrained = await DrainOutputAsync(
            outputTask,
            errorTask,
            outputCapture,
            request.CleanupTimeout,
            recorder,
            rootPid).ConfigureAwait(false);
        if (!streamsDrained)
        {
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
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

    private static async Task StopAsync(
        Process process,
        TimeSpan cleanupTimeout,
        FlightRecorder recorder)
    {
        await recorder.RecordAsync("bounded_stop_requested", pid: process.Id).ConfigureAwait(false);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var cleanup = new CancellationTokenSource(cleanupTimeout);
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            await recorder.RecordAsync(
                "process_exit",
                pid: process.Id,
                exitCode: process.ExitCode).ConfigureAwait(false);
            await recorder.RecordAsync("cleanup_completed", pid: process.Id).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: process.Id,
                detail: exception.Message).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DrainOutputAsync(
        Task outputTask,
        Task errorTask,
        CancellationTokenSource outputCapture,
        TimeSpan cleanupTimeout,
        FlightRecorder recorder,
        int rootPid)
    {
        var drain = Task.WhenAll(outputTask, errorTask);
        try
        {
            await drain.WaitAsync(cleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            await outputCapture.CancelAsync().ConfigureAwait(false);
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
