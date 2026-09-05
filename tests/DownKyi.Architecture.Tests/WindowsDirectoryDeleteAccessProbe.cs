using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Architecture.Tests;

internal sealed class WindowsDirectoryDeleteAccessProbe : IDisposable
{
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
    private readonly List<string> transitions = [];
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

    private WindowsDirectoryDeleteAccessProbe(
        string directory,
        int testhostProcessId)
    {
        this.directory = directory;
        flightRecorder = WindowsEtwResourceFlightRecorder.Start(
            directory,
            testhostProcessId);
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Fixture.Tests DELETE-access canary",
        };
        worker.Start();
    }

    internal bool RelockDetected => Volatile.Read(ref relockDetected) != 0;
    internal bool BlockedAtCleanupReturn => Volatile.Read(ref blockedAtCleanupReturn) != 0;
    internal bool AnomalyDetected => RelockDetected || BlockedAtCleanupReturn;

    internal static WindowsDirectoryDeleteAccessProbe Start(
        string directory,
        int testhostProcessId)
    {
        var probe = new WindowsDirectoryDeleteAccessProbe(
            directory,
            testhostProcessId);
        if (!probe.firstSample.Wait(TimeSpan.FromSeconds(2)))
        {
            probe.Dispose();
            throw new TimeoutException("The DELETE-access canary did not produce its first sample.");
        }

        return probe;
    }

    internal void AddKnownProcessId(int processId)
    {
        flightRecorder.AddKnownProcessId(processId);
    }

    internal void MarkCancellationRequested()
    {
        cancellationRequestedUtc = DateTimeOffset.UtcNow;
        Volatile.Write(ref cancellationRequested, 1);
        lock (evidenceLock)
        {
            transitions.Add($"T+{stopwatch.Elapsed.TotalMilliseconds:F3}ms cancellation-requested");
        }
    }

    internal void MarkCleanupReturned()
    {
        var state = TryAcquireDeleteAccess(directory);
        Count(state);
        lock (evidenceLock)
        {
            transitions.Add(
                $"T+{stopwatch.Elapsed.TotalMilliseconds:F3}ms cleanup-returned " +
                $"state={state.State} win32Error={state.Win32Error}");
        }

        if (state.State == ProbeState.SharingViolation)
        {
            Volatile.Write(ref blockedAtCleanupReturn, 1);
        }
    }

    internal async Task ObservePostCleanupAsync(TimeSpan observationWindow)
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

    internal string StopAndFormat(bool forcePreserve = false)
    {
        RequestStop();
        var stopped = worker.Join(TimeSpan.FromSeconds(2));
        var lifecycle = flightRecorder.StopAndFormat(forcePreserve || AnomalyDetected);
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
                .Append(" workerStopped=").Append(stopped)
                .Append(" workerFailure=").Append(workerFailure ?? "none");
            foreach (var transition in transitions)
            {
                builder.AppendLine().Append("transition ").Append(transition);
            }

            builder.AppendLine().Append("resourceFlightRecorder:").AppendLine().Append(lifecycle);
            return builder.ToString();
        }
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
        ProbeResult? previous = null;
        try
        {
            while (Volatile.Read(ref stopRequested) == 0)
            {
                var current = TryAcquireDeleteAccess(directory);
                Count(current);
                if (previous is null || current != previous)
                {
                    RecordTransition(current);
                    if (previous?.State == ProbeState.Allowed &&
                        current.State == ProbeState.SharingViolation &&
                        Volatile.Read(ref cancellationRequested) != 0)
                    {
                        Volatile.Write(ref relockDetected, 1);
                    }

                    if (current.State == ProbeState.Allowed && AnomalyDetected)
                    {
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

    private void Count(ProbeResult result)
    {
        switch (result.State)
        {
            case ProbeState.Allowed:
                Interlocked.Increment(ref allowedSamples);
                break;
            case ProbeState.SharingViolation:
                Interlocked.Increment(ref blockedSamples);
                break;
            default:
                Interlocked.Increment(ref otherErrorSamples);
                break;
        }
    }

    private void RecordTransition(ProbeResult result)
    {
        lock (evidenceLock)
        {
            transitions.Add(
                $"T+{stopwatch.Elapsed.TotalMilliseconds:F3}ms state={result.State} win32Error={result.Win32Error}");
        }
    }

    private void RequestStop()
    {
        Volatile.Write(ref stopRequested, 1);
    }

    private static ProbeResult TryAcquireDeleteAccess(string directory)
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
            return new ProbeResult(ProbeState.Allowed, 0);
        }

        var error = Marshal.GetLastPInvokeError();
        return error is SharingViolation or LockViolation
            ? new ProbeResult(ProbeState.SharingViolation, error)
            : new ProbeResult(ProbeState.OtherError, error);
    }

    private enum ProbeState
    {
        Allowed,
        SharingViolation,
        OtherError,
    }

    private sealed record ProbeResult(ProbeState State, int Win32Error);

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
