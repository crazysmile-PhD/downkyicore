using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.TestInfrastructure;

public enum DeleteAccessState
{
    Allowed,
    SharingViolation,
    OtherError,
}

public sealed record DeleteAccessProbeResult(DeleteAccessState State, int Win32Error);

public sealed class TargetedResourceForensics : IDisposable
{
    private const int TransitionCapacity = 256;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private readonly string directory;
    private readonly WindowsEtwResourceFlightRecorder flightRecorder;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly ManualResetEventSlim firstSample = new(false);
    private readonly TaskCompletionSource allowedAfterAnomaly = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock evidenceLock = new();
    private readonly Queue<string> transitions = [];
    private readonly Thread worker;
    private long allowedSamples;
    private long blockedSamples;
    private long otherErrorSamples;
    private int stopRequested;
    private int cancellationRequested;
    private int relockDetected;
    private int blockedAtCleanupReturn;
    private string? workerFailure;
    private DateTimeOffset? cancellationRequestedUtc;
    private DateTimeOffset? cleanupReturnedUtc;
    private DateTimeOffset? anomalyDetectedUtc;
    private DateTimeOffset? readyForOperationUtc;

    private TargetedResourceForensics(
        string directory,
        string testIdentity,
        int testhostProcessId)
    {
        this.directory = Path.GetFullPath(directory);
        flightRecorder = WindowsEtwResourceFlightRecorder.Start(
            this.directory,
            testIdentity,
            testhostProcessId);
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Targeted DELETE-access probe",
        };
        worker.Start();
    }

    public bool RelockDetected => Volatile.Read(ref relockDetected) != 0;
    public bool BlockedAtCleanupReturn => Volatile.Read(ref blockedAtCleanupReturn) != 0;
    public bool AnomalyDetected => RelockDetected || BlockedAtCleanupReturn;
    public string? ArtifactPath => flightRecorder.ArtifactPath;

    public static TargetedResourceForensics Start(
        string directory,
        string testIdentity,
        int testhostProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(testIdentity);
        var probe = new TargetedResourceForensics(
            directory,
            testIdentity,
            testhostProcessId);
        if (!probe.firstSample.Wait(TimeSpan.FromSeconds(2)))
        {
            probe.Dispose();
            throw new TimeoutException("The DELETE-access canary did not produce its first sample.");
        }

        return probe;
    }

    [SupportedOSPlatform("windows")]
    public static DeleteAccessProbeResult ProbeDeleteAccess(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return TryAcquireDeleteAccess(Path.GetFullPath(directory));
    }

    public void AddKnownProcessId(int processId, string classification = "known")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        flightRecorder.AddKnownProcessId(processId, classification);
    }

    public void MarkCancellationRequested()
    {
        cancellationRequestedUtc = DateTimeOffset.UtcNow;
        Volatile.Write(ref cancellationRequested, 1);
        lock (evidenceLock)
        {
            EnqueueTransition($"cancellation-requested");
        }
    }

    public DeleteAccessProbeResult MarkCleanupReturned()
    {
        var state = TryAcquireDeleteAccess(directory);
        cleanupReturnedUtc = DateTimeOffset.UtcNow;
        Count(state);
        lock (evidenceLock)
        {
            EnqueueTransition(
                $"cleanup-returned state={state.State} win32Error={state.Win32Error}");
            if (state.State == DeleteAccessState.SharingViolation)
            {
                anomalyDetectedUtc ??= DateTimeOffset.UtcNow;
            }
        }

        if (state.State == DeleteAccessState.SharingViolation)
        {
            Volatile.Write(ref blockedAtCleanupReturn, 1);
        }

        return state;
    }

    public async Task ObservePostCleanupAsync(TimeSpan observationWindow)
    {
        using var deadline = new CancellationTokenSource(observationWindow);
        try
        {
            await allowedAfterAnomaly.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
    }

    public string StopAndFormat(
        bool forcePreserve = false,
        string rootCauseStatus = "Root cause not proven.")
    {
        RequestStop();
        var stopped = worker.Join(TimeSpan.FromSeconds(2));
        string probeEvidence;
        lock (evidenceLock)
        {
            var builder = new StringBuilder();
            builder.Append("probeStartedUtc=").Append(startedUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append(" durationMs=").Append(stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" allowedSamples=").Append(allowedSamples)
                .Append(" blockedSamples=").Append(blockedSamples)
                .Append(" otherErrorSamples=").Append(otherErrorSamples)
                .Append(" relockDetected=").Append(RelockDetected)
                .Append(" blockedAtCleanupReturn=").Append(BlockedAtCleanupReturn)
                .Append(" cancellationRequestedUtc=")
                .Append(cancellationRequestedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "not-marked")
                .Append(" cleanupReturnedUtc=")
                .Append(cleanupReturnedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "not-marked")
                .Append(" failureOrAnomalyUtc=")
                .Append(anomalyDetectedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "not-observed")
                .Append(" readyForOperationUtc=")
                .Append(readyForOperationUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "not-observed")
                .Append(" workerStopped=").Append(stopped)
                .Append(" workerFailure=").Append(workerFailure ?? "none")
                .Append(" rootCauseStatus=").Append(rootCauseStatus);
            foreach (var transition in transitions)
            {
                builder.AppendLine().Append("transition ").Append(transition);
            }

            probeEvidence = builder.ToString();
        }

        var lifecycle = flightRecorder.StopAndFormat(
            forcePreserve || AnomalyDetected,
            probeEvidence,
            rootCauseStatus);
        return $"{probeEvidence}{Environment.NewLine}resourceFlightRecorder:{Environment.NewLine}{lifecycle}";
    }

    public void Dispose()
    {
        RequestStop();
        _ = worker.Join(TimeSpan.FromSeconds(2));
        flightRecorder.Dispose();
        firstSample.Dispose();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A canary worker failure must be reported as evidence instead of terminating the testhost.")]
    private void Run()
    {
        DeleteAccessProbeResult? previous = null;
        try
        {
            while (Volatile.Read(ref stopRequested) == 0)
            {
                var current = TryAcquireDeleteAccess(directory);
                Count(current);
                if (previous is null || current != previous)
                {
                    RecordTransition(current);
                    if (previous?.State == DeleteAccessState.Allowed &&
                        current.State == DeleteAccessState.SharingViolation &&
                        Volatile.Read(ref cancellationRequested) != 0)
                    {
                        lock (evidenceLock)
                        {
                            anomalyDetectedUtc ??= DateTimeOffset.UtcNow;
                        }

                        Volatile.Write(ref relockDetected, 1);
                    }

                    if (current.State == DeleteAccessState.Allowed && AnomalyDetected)
                    {
                        readyForOperationUtc ??= DateTimeOffset.UtcNow;
                        allowedAfterAnomaly.TrySetResult();
                    }

                    previous = current;
                }

                firstSample.Set();
                Thread.Yield();
            }
        }
        catch (Exception exception)
        {
            lock (evidenceLock)
            {
                workerFailure = $"{exception.GetType().FullName}: {exception.Message}";
            }

            firstSample.Set();
            allowedAfterAnomaly.TrySetResult();
        }
    }

    private void Count(DeleteAccessProbeResult result)
    {
        switch (result.State)
        {
            case DeleteAccessState.Allowed:
                Interlocked.Increment(ref allowedSamples);
                break;
            case DeleteAccessState.SharingViolation:
                Interlocked.Increment(ref blockedSamples);
                break;
            default:
                Interlocked.Increment(ref otherErrorSamples);
                break;
        }
    }

    private void RecordTransition(DeleteAccessProbeResult result)
    {
        lock (evidenceLock)
        {
            EnqueueTransition($"state={result.State} win32Error={result.Win32Error}");
        }
    }

    private void EnqueueTransition(string detail)
    {
        while (transitions.Count >= TransitionCapacity)
        {
            _ = transitions.Dequeue();
        }

        transitions.Enqueue(
            $"utc={DateTimeOffset.UtcNow:O} T+{stopwatch.Elapsed.TotalMilliseconds:F3}ms {detail}");
    }

    private void RequestStop()
    {
        Volatile.Write(ref stopRequested, 1);
    }

    private static DeleteAccessProbeResult TryAcquireDeleteAccess(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DELETE-access canary is Windows-only.");
        }

        using var handle = NativeMethods.CreateFile(
            directory,
            NativeMethods.DeleteAccess,
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.BackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return new DeleteAccessProbeResult(DeleteAccessState.Allowed, 0);
        }

        var error = Marshal.GetLastPInvokeError();
        return error is SharingViolation or LockViolation
            ? new DeleteAccessProbeResult(DeleteAccessState.SharingViolation, error)
            : new DeleteAccessProbeResult(DeleteAccessState.OtherError, error);
    }

    private static class NativeMethods
    {
        internal const uint DeleteAccess = 0x00010000;
        internal const uint ShareRead = 0x00000001;
        internal const uint ShareWrite = 0x00000002;
        internal const uint ShareDelete = 0x00000004;
        internal const uint OpenExisting = 3;
        internal const uint BackupSemantics = 0x02000000;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
