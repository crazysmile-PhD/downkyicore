using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.CentralTestRunner;

internal sealed record ProcessExecutionRequest(
    string SliceIdentity,
    string TestIdentity,
    ProcessStartInfo StartInfo,
    TimeSpan Timeout,
    TimeSpan CleanupTimeout,
    string EvidenceDirectory,
    Func<int, TimeSpan, FinalProcessSnapshot>? SnapshotCapture = null,
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
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Async disposal retains the typed owner in this console runner.")]
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
        var ownerStarted = false;

        try
        {
            await using var owner = await ProcessLifecycleOwner.StartAsync(
                    request.StartInfo, TimeSpan.FromSeconds(5), cleanupWindow: request.CleanupTimeout)
                .ConfigureAwait(false);
            ownerStarted = true;
            owner.StartOutputCapture(standardOutput, standardError, recorder.Redactor,
                Console.Out, request.ErrorDestination ?? Console.Error);

            var rootPid = owner.RootPid;
            DateTimeOffset rootStartTime = default;
            Exception? identityFailure = null;
            try
            {
                rootStartTime = request.RootStartTimeReader?.Invoke(owner.Host) ?? owner.RootStartTimeUtc;
                recorder.SetRootIdentity(rootPid, rootStartTime);
                recorder.RecordInMemory("process_start", pid: rootPid, startTimeUtc: rootStartTime);
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
            {
                identityFailure = exception;
                recorder.RecordInMemory("root_identity_failed", pid: rootPid,
                    detail: exception.Message);
            }

            var outcome = await owner.CompleteAsync(
                request.Timeout,
                cancellationToken,
                initialFailure: identityFailure,
                onPhase: phase => recorder.RecordInMemory(phase, pid: rootPid))
                .ConfigureAwait(false);

            recorder.SetLifecycleFailure(owner);
            recorder.RecordInMemory("process_exit", pid: rootPid, exitCode: outcome.RootExitCode);
            if (outcome.LiveEvidence is not null)
            {
                recorder.RecordInMemory("live_observation", pid: rootPid,
                    detail: outcome.LiveEvidence);
            }
            if (outcome.SecondaryCleanupFailure is not null)
            {
                recorder.RecordInMemory("secondary_cleanup_failed", pid: rootPid,
                    detail: outcome.SecondaryCleanupFailure.Message);
            }

            var exitCode = MapExitCode(outcome);
            recorder.SetOutputTails(standardOutput.Value, standardError.Value);
            if (exitCode != 0)
            {
                var reportOutcome = identityFailure is not null
                    ? "root_identity_failed"
                    : !outcome.CleanupSucceeded
                        ? outcome.OutputHeld ? "stream_drain_failed" : "cleanup_failed"
                        : outcome.Trigger switch
                        {
                            ProcessLifecycleTrigger.Cancelled => "cancellation",
                            ProcessLifecycleTrigger.TimedOut => "timeout",
                            ProcessLifecycleTrigger.RootExited => "process_exit",
                            _ => "process_failed"
                        };
                // Diagnostic capture follows mandatory cleanup and cannot authorize it.
                await recorder.FinalizeFailureAsync(
                    reportOutcome, standardOutput, standardError, owner.BeginCleanup())
                    .ConfigureAwait(false);
            }

            return new ProcessExecutionResult(
                exitCode, rootPid, rootStartTime, recorder.EvidencePath, recorder);
        }
        catch (Exception exception) when (!ownerStarted &&
                                          exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception or
                                              TimeoutException or IOException or AggregateException)
        {
            if (exception is AggregateException { InnerExceptions.Count: 2 } aggregate)
            {
                recorder.SetStartupFailure(aggregate.InnerExceptions[0],
                    aggregate.InnerExceptions[1]);
            }
            await recorder.RecordAsync("process_start_failed", detail: exception.Message)
                .ConfigureAwait(false);
            await recorder.CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);
            await recorder.FinalizeFailureAsync(
                "start_failed", standardOutput, standardError).ConfigureAwait(false);
            return new ProcessExecutionResult(2, 0, default, recorder.EvidencePath, recorder);
        }
    }

    private static int MapExitCode(ProcessLifecycleOutcome outcome)
    {
        if (!outcome.CleanupSucceeded || outcome.OutputHeld)
        {
            return 2;
        }

        return outcome.Trigger switch
        {
            ProcessLifecycleTrigger.RootExited => outcome.RootExitCode,
            ProcessLifecycleTrigger.Cancelled => 130,
            ProcessLifecycleTrigger.TimedOut => 124,
            _ => 2
        };
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
}
