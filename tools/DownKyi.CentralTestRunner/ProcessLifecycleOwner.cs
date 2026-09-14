using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

internal enum ProcessLifecycleTrigger
{
    RootExited,
    Cancelled,
    TimedOut,
    Faulted
}

internal sealed record ProcessLifecycleOutcome(
    ProcessLifecycleTrigger Trigger,
    int RootExitCode,
    bool HostExited,
    bool CleanupSucceeded,
    bool OutputHeld,
    Exception? PrimaryFailure,
    string? LiveEvidence,
    Exception? SecondaryCleanupFailure);

// One invocation owns one kernel containment. The host enters it before launch;
// on Unix, the host stays live until group cleanup and then the owner reaps it.
internal sealed class ProcessLifecycleOwner : IAsyncDisposable
{
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;
    private readonly SafeFileHandle? job;
    private readonly LinuxCgroupContainment? cgroup;
    private readonly NamedPipeServerStream control;
    private readonly StreamReader controlReader;
    private readonly Task controlReadTask;
    private readonly TaskCompletionSource<int?> rootExitReport =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool?> outputRelayReport =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamReader? standardOutputReader;
    private readonly StreamReader? standardErrorReader;
    private readonly TimeSpan cleanupWindow;
    private CleanupDeadline? cleanupDeadline;
    private CancellationTokenSource? outputCancellation;
    private Task? outputTask;
    private Task? errorTask;
    private bool rootExitRead;
    private Task<ProcessLifecycleOutcome>? completionTask;
    private Exception? primaryFailure;
    private int cleanupFailed;
    private readonly object secondaryFailureGate = new();
    private bool disposed;

    private ProcessLifecycleOwner(Process host, SafeFileHandle? job,
        LinuxCgroupContainment? cgroup, NamedPipeServerStream control,
        StreamReader controlReader, ScopeHandshake handshake, TimeSpan cleanupWindow,
        StreamReader? standardOutputReader = null, StreamReader? standardErrorReader = null,
        bool observeControl = true)
    {
        Host = host;
        this.job = job;
        this.cgroup = cgroup;
        this.control = control;
        this.controlReader = controlReader;
        controlReadTask = observeControl ? ReadControlAsync() : Task.CompletedTask;
        this.standardOutputReader = standardOutputReader;
        this.standardErrorReader = standardErrorReader;
        this.cleanupWindow = cleanupWindow;
        RootPid = handshake.Pid;
        RootStartTimeUtc = handshake.StartTimeUtc;
    }

    internal Process Host { get; }
    internal int RootPid { get; }
    internal DateTimeOffset RootStartTimeUtc { get; }
    internal SafeFileHandle? WindowsJobHandle => job;
    internal bool UsesLinuxCgroup => cgroup is not null;
    internal int RootExitCode { get; private set; }
    internal CleanupDeadline BeginCleanup() => cleanupDeadline ??= new CleanupDeadline(cleanupWindow);
    internal bool OutputCaptureTasksTerminal =>
        outputTask?.IsCompleted == true && errorTask?.IsCompleted == true;
    internal bool OwnedTasksTerminal => controlReadTask.IsCompleted &&
        (outputTask is null || outputTask.IsCompleted) &&
        (errorTask is null || errorTask.IsCompleted);
    internal Exception? PrimaryFailure => Volatile.Read(ref primaryFailure);
    internal string? LiveEvidence { get; private set; }
    internal Exception? SecondaryCleanupFailure { get; private set; }
    private void RecordPrimaryFailure(Exception failure) => RecordCleanupFailure(failure);
    private void RecordTerminalFailure(Exception failure) =>
        Interlocked.CompareExchange(ref primaryFailure, failure, null);
    internal void RecordObservedFailure(Exception failure) => RecordCleanupFailure(failure);

    internal void StartOutputCapture(
        TailBuffer standardOutput, TailBuffer standardError, SensitiveEvidenceRedactor redactor)
    {
        if ((standardOutputReader is null && !Host.StartInfo.RedirectStandardOutput) ||
            outputCancellation is not null)
        {
            throw new InvalidOperationException("Output capture is unavailable or already started.");
        }

        outputCancellation = new CancellationTokenSource();
        outputTask = BoundedOutputCapture.CaptureAsync(
            standardOutputReader ?? Host.StandardOutput, standardOutput, redactor, outputCancellation.Token);
        errorTask = BoundedOutputCapture.CaptureAsync(
            standardErrorReader ?? Host.StandardError, standardError, redactor, outputCancellation.Token);
    }

    private async Task<bool> WaitForOutputWithinAsync(TimeSpan window)
    {
        var drain = Task.WhenAll(outputTask!, errorTask!);
        try
        {
            await drain.WaitAsync(window).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // The drain remains registered with this owner and is joined later.
            RecordPrimaryFailure(new TimeoutException(
                "An owned descendant held the output pipe after the root exited."));
            return false;
        }
        catch (Exception exception)
        {
            RecordPrimaryFailure(exception);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "All stream cancellation and disposal steps must run before joining the owned capture tasks.")]
    private async Task<bool> JoinOutputAsync(CleanupDeadline deadline)
    {
        var drain = Task.WhenAll(outputTask!, errorTask!);
        try
        {
            await drain.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            RecordCleanupFailure(new TimeoutException(
                "Owned output capture exceeded the cleanup deadline."));
            try { await outputCancellation!.CancelAsync().ConfigureAwait(false); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            try { (standardOutputReader ?? Host.StandardOutput).Dispose(); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            try { (standardErrorReader ?? Host.StandardError).Dispose(); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            // A deadline failure does not transfer ownership of these tasks.
            // Both pipe reads must be terminal before this owner can return.
            try { await drain.ConfigureAwait(false); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            return false;
        }
        catch (Exception exception)
        {
            RecordPrimaryFailure(exception);
            throw;
        }
    }

    private async Task WaitForResourceRundownAsync(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        try
        {
            await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                directory, BeginCleanup().Remaining).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordPrimaryFailure(exception);
            throw;
        }
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "The catch also handles failures before ownership transfer in this async launch.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The pipe and reader transfer to the returned owner; failed launches dispose them in finally.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup failure must remain primary if mandatory containment cleanup also fails.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Async disposal retains the startup recovery owner in this console runner.")]
    internal static async Task<ProcessLifecycleOwner> StartAsync(
        ProcessStartInfo testStartInfo, TimeSpan startupWindow,
        bool redirectOutput = true, TimeSpan? cleanupWindow = null)
    {
        // Unix named pipes include the temporary directory in a short socket path.
        var pipeName = Guid.NewGuid().ToString("N");
        var unixOutputRelay = redirectOutput && !OperatingSystem.IsWindows();
        var outputPipeName = unixOutputRelay ? Guid.NewGuid().ToString("N") : null;
        var errorPipeName = unixOutputRelay ? Guid.NewGuid().ToString("N") : null;
        NamedPipeServerStream? control = null;
        NamedPipeServerStream? outputPipe = null;
        NamedPipeServerStream? errorPipe = null;
        var jobName = OperatingSystem.IsWindows() ? $"Local\\downkyi-test-{Guid.NewGuid():N}" : null;
        SafeFileHandle? job = null;
        LinuxCgroupContainment? cgroup = null;
        Process? host = null;
        StreamReader? reader = null;
        StreamReader? outputReader = null;
        StreamReader? errorReader = null;
        var containmentReady = false;
        using var startupCancellation = new CancellationTokenSource(startupWindow);
        try
        {
            control = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            if (unixOutputRelay)
            {
                outputPipe = new NamedPipeServerStream(outputPipeName!, PipeDirection.In,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                errorPipe = new NamedPipeServerStream(errorPipeName!, PipeDirection.In,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            if (jobName is not null)
            {
                job = CreateWindowsJob(jobName);
            }

            var hostInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = redirectOutput && !unixOutputRelay,
                RedirectStandardError = redirectOutput && !unixOutputRelay,
                CreateNoWindow = true
            };
            hostInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            hostInfo.ArgumentList.Add("owned-scope-host");
            hostInfo.ArgumentList.Add(pipeName);
            hostInfo.ArgumentList.Add(jobName ?? "-");
            host = new Process { StartInfo = hostInfo };
            if (!host.Start())
            {
                throw new InvalidOperationException("The ownership host did not start.");
            }

            if (OperatingSystem.IsLinux())
            {
                LinuxCgroupContainment.TryCreateForHost(host.Id, out cgroup);
            }

            await control.WaitForConnectionAsync(startupCancellation.Token).ConfigureAwait(false);
            reader = new StreamReader(control);
            var ready = await reader.ReadLineAsync(startupCancellation.Token).ConfigureAwait(false);
            if (!string.Equals(ready, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The ownership host did not enter containment: {ready}");
            }
            containmentReady = true;

            var launch = new ScopeLaunch(
                testStartInfo.FileName,
                [.. testStartInfo.ArgumentList],
                testStartInfo.WorkingDirectory,
                new Dictionary<string, string?>(testStartInfo.Environment),
                outputPipeName, errorPipeName);
            await host.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(launch).AsMemory(), startupCancellation.Token).ConfigureAwait(false);
            if (unixOutputRelay)
            {
                await outputPipe!.WaitForConnectionAsync(startupCancellation.Token).ConfigureAwait(false);
                outputReader = new StreamReader(outputPipe);
                outputPipe = null;
                await errorPipe!.WaitForConnectionAsync(startupCancellation.Token).ConfigureAwait(false);
                errorReader = new StreamReader(errorPipe);
                errorPipe = null;
            }
            var line = await reader.ReadLineAsync(startupCancellation.Token).ConfigureAwait(false);
            var handshake = line is null ? null : JsonSerializer.Deserialize<ScopeHandshake>(line);
            if (handshake is null || handshake.Error is not null || handshake.Pid <= 0)
            {
                throw new InvalidOperationException($"The ownership scope did not launch the test: {handshake?.Error ?? "no handshake"}");
            }

            var scope = new ProcessLifecycleOwner(host, job, cgroup, control, reader, handshake,
                cleanupWindow ?? startupWindow, outputReader, errorReader);
            host = null;
            job = null;
            cgroup = null;
            reader = null;
            control = null;
            outputReader = null;
            errorReader = null;
            return scope;
        }
        catch (Exception launchFailure)
        {
            Exception? cleanupFailure = null;
            void RecordStartupCleanupFailure(Exception failure) =>
                cleanupFailure = cleanupFailure is null
                    ? failure
                    : new AggregateException(cleanupFailure, failure);
            try
            {
                if (host is not null && containmentReady && reader is not null && control is not null)
                {
                    // The host has acknowledged containment, so the same owner
                    // can kill, verify quiescence and reap even without a root handshake.
                    await using var recovery = new ProcessLifecycleOwner(
                        host, job, cgroup, control, reader,
                        new ScopeHandshake(0, default, null), cleanupWindow ?? startupWindow,
                        outputReader, errorReader, observeControl: false);
                    host = null;
                    job = null;
                    cgroup = null;
                    reader = null;
                    control = null;
                    outputReader = null;
                    errorReader = null;
                    var recoveryOutcome = await recovery.CompleteAsync(
                        TimeSpan.Zero, CancellationToken.None,
                        initialFailure: launchFailure).ConfigureAwait(false);
                    cleanupFailure = recoveryOutcome.SecondaryCleanupFailure;
                }
                else if (host is not null)
                {
                    // No launch request can have run before the ready handshake.
                    try
                    {
                        if (job is not null)
                        {
                            TerminateWindowsJob(job);
                        }
                        else
                        {
                            cgroup?.Kill();
                        }
                    }
                    catch (Exception exception)
                    {
                        RecordStartupCleanupFailure(exception);
                    }
                    try
                    {
                        if (!host.HasExited)
                        {
                            host.Kill();
                        }
                    }
                    catch (Exception exception)
                    {
                        RecordStartupCleanupFailure(exception);
                    }
                    try { await host.WaitForExitAsync().ConfigureAwait(false); }
                    catch (Exception exception) { RecordStartupCleanupFailure(exception); }
                    if (cgroup is not null)
                    {
                        try { cgroup.Remove(); }
                        catch (Exception exception) { RecordStartupCleanupFailure(exception); }
                    }
                }
            }
            catch (Exception exception)
            {
                RecordStartupCleanupFailure(exception);
            }

            var primary = launchFailure is OperationCanceledException &&
                startupCancellation.IsCancellationRequested
                ? new TimeoutException("The ownership host exceeded the startup deadline.", launchFailure)
                : launchFailure;
            if (cleanupFailure is not null)
            {
                throw new AggregateException(primary, cleanupFailure);
            }
            if (!ReferenceEquals(primary, launchFailure))
            {
                throw primary;
            }
            throw;
        }
        finally
        {
            host?.Dispose();
            job?.Dispose();
            cgroup?.Dispose();
            if (reader is not null)
            {
                reader.Dispose();
            }
            outputReader?.Dispose();
            errorReader?.Dispose();
            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            if (outputPipe is not null)
            {
                await outputPipe.DisposeAsync().ConfigureAwait(false);
            }
            if (errorPipe is not null)
            {
                await errorPipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The control reader must complete both reports and remain joined by the owner after any pipe failure.")]
    private async Task ReadControlAsync()
    {
        try
        {
            string? line;
            while ((line = await controlReader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var exitCode))
                {
                    if (exitCode != 0)
                    {
                        // Commit the root result before the reader can observe
                        // a later relay or teardown error on this same channel.
                        RecordTerminalFailure(new InvalidOperationException(
                            $"The owned process exited with code {exitCode}."));
                    }
                    rootExitReport.TrySetResult(exitCode);
                }
                else if (string.Equals(line, "output_complete", StringComparison.Ordinal))
                {
                    outputRelayReport.TrySetResult(true);
                }
                else if (string.Equals(line, "output_failed", StringComparison.Ordinal))
                {
                    RecordCleanupFailure(new IOException("The ownership host output relay failed."));
                    outputRelayReport.TrySetResult(false);
                    rootExitReport.TrySetResult(null);
                }
                else
                {
                    RecordCleanupFailure(new IOException($"Unexpected ownership host report: {line}"));
                    outputRelayReport.TrySetResult(false);
                    rootExitReport.TrySetResult(null);
                }
            }
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(exception);
        }
        finally
        {
            rootExitReport.TrySetResult(null);
            outputRelayReport.TrySetResult(null);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Both pipe disposal attempts must run and the owned control task must be joined after either failure.")]
    private async Task<bool> JoinControlAsync(CleanupDeadline deadline)
    {
        try
        {
            await controlReadTask.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            RecordCleanupFailure(new TimeoutException(
                "The ownership host control reader exceeded the cleanup deadline."));
            try { controlReader.Dispose(); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            try { await control.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
            await controlReadTask.ConfigureAwait(false);
            return false;
        }
    }

    private async Task WaitForRootExitAsync(CancellationToken cancellationToken)
    {
        if (rootExitRead)
        {
            return;
        }

        var exitCode = await rootExitReport.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw PrimaryFailure ??
                new InvalidOperationException("The ownership host did not report the root exit code.");
        }

        RootExitCode = exitCode.Value;
        rootExitRead = true;
    }

    internal Task<ProcessLifecycleOutcome> CompleteAsync(
        TimeSpan operationTimeout,
        CancellationToken cancellationToken,
        string? cleanupResourceDirectory = null,
        Exception? initialFailure = null,
        Action<string>? onPhase = null,
        Func<int, string?>? unixGroupObserver = null)
    {
        if (operationTimeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(operationTimeout, TimeSpan.Zero);
        }
        return completionTask ??= CompleteCoreAsync(
            operationTimeout, cleanupResourceDirectory, initialFailure,
            onPhase, unixGroupObserver, cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The lifecycle owner must finish mandatory cleanup after every observer or platform failure.")]
    private async Task<ProcessLifecycleOutcome> CompleteCoreAsync(
        TimeSpan operationTimeout,
        string? cleanupResourceDirectory,
        Exception? initialFailure,
        Action<string>? onPhase,
        Func<int, string?>? unixGroupObserver,
        CancellationToken cancellationToken)
    {
        void Phase(string name)
        {
            try { onPhase?.Invoke(name); }
            catch (Exception exception)
            {
                // Recorder callbacks observe the owner; they cannot determine
                // whether containment cleanup succeeded or become root cause.
                RecordSecondaryFailure(exception);
            }
        }

        var trigger = ProcessLifecycleTrigger.Faulted;
        if (initialFailure is not null)
        {
            RecordObservedFailure(initialFailure);
            Phase("fault");
        }
        else
        {
            Phase("process_wait_begin");
            using var timeout = new CancellationTokenSource(operationTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            try
            {
                await WaitForRootExitAsync(linked.Token).ConfigureAwait(false);
                trigger = ProcessLifecycleTrigger.RootExited;
                Phase("process_exited");
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                trigger = cancellationToken.IsCancellationRequested
                    ? ProcessLifecycleTrigger.Cancelled
                    : ProcessLifecycleTrigger.TimedOut;
                Phase(trigger == ProcessLifecycleTrigger.Cancelled ? "cancellation" : "timeout");
            }
            catch (Exception exception)
            {
                RecordPrimaryFailure(exception);
                Phase("fault");
            }
        }

        // Timeout is committed before cleanup starts. Nonzero root exit is
        // committed by the control reader before it publishes that result.
        if (trigger == ProcessLifecycleTrigger.TimedOut)
        {
            RecordTerminalFailure(new TimeoutException("The owned process exceeded its execution deadline."));
        }

        var deadline = BeginCleanup();
        var outputHeld = false;
        if (trigger == ProcessLifecycleTrigger.RootExited &&
            outputTask is not null && errorTask is not null)
        {
            try
            {
                outputHeld = !await WaitForOutputWithinAsync(
                    deadline.PostExitDrainWindow).ConfigureAwait(false);
                if (outputHeld)
                {
                    Phase("post_exit_output_held");
                }
                else if (standardOutputReader is not null)
                {
                    // EOF alone does not prove the host's relays succeeded.
                    // Its status is ordered after both relay tasks terminate.
                    using var statusCancellation = new CancellationTokenSource(deadline.WorkWindow);
                    var status = await outputRelayReport.Task.WaitAsync(statusCancellation.Token)
                        .ConfigureAwait(false);
                    if (status != true)
                    {
                        throw new IOException("The ownership host did not complete output relay.");
                    }
                }
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception);
            }
        }

        var stopped = false;
        Phase("bounded_stop_requested");
        Phase("terminate_begin");
        try
        {
            await TerminateAsync(deadline, unixGroupObserver).ConfigureAwait(false);
            stopped = true;
            Phase("terminate_returned");
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(exception);
        }

        var controlJoined = false;
        try
        {
            controlJoined = await JoinControlAsync(deadline).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(exception);
        }

        var resourceReady = true;
        if (OperatingSystem.IsWindows() && trigger != ProcessLifecycleTrigger.RootExited &&
            cleanupResourceDirectory is not null)
        {
            try
            {
                await WaitForResourceRundownAsync(cleanupResourceDirectory).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                resourceReady = false;
                RecordCleanupFailure(exception);
            }
        }

        var drained = true;
        if (outputTask is not null && errorTask is not null)
        {
            Phase("drain_begin");
            try
            {
                drained = await JoinOutputAsync(deadline).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                drained = false;
                RecordCleanupFailure(exception);
            }
            Phase("drain_returned");
        }

        bool hostExited;
        try { hostExited = Host.HasExited; }
        catch (Exception exception)
        {
            hostExited = false;
            RecordCleanupFailure(exception);
        }
        if (!hostExited)
        {
            RecordCleanupFailure(new InvalidOperationException(
                "The ownership host remained live at lifecycle completion."));
            // If the OS refused termination, returning a failed outcome with a
            // live helper would still violate structured concurrency. This
            // exceptional path may outlive the deadline, but never reports 130.
            await Host.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            hostExited = true;
        }

        await ReleaseResourcesAsync().ConfigureAwait(false);
        if (deadline.Remaining == TimeSpan.Zero)
        {
            RecordCleanupFailure(new TimeoutException(
                "Process lifecycle cleanup exceeded its shared deadline."));
        }

        var succeeded = stopped && resourceReady && drained && controlJoined && hostExited &&
            Volatile.Read(ref cleanupFailed) == 0;
        if (succeeded)
        {
            Phase("cleanup_completed");
            succeeded = Volatile.Read(ref cleanupFailed) == 0;
        }
        if (!succeeded)
        {
            Phase("cleanup_failed");
        }
        return new ProcessLifecycleOutcome(
            trigger, RootExitCode, hostExited, succeeded, outputHeld,
            PrimaryFailure, LiveEvidence, SecondaryCleanupFailure);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every owned handle must be released and each teardown error retained in the terminal outcome.")]
    private async Task ReleaseResourcesAsync()
    {
        void Release(Action dispose)
        {
            try { dispose(); }
            catch (Exception exception) { RecordCleanupFailure(exception); }
        }

        Release(controlReader.Dispose);
        Release(() => standardOutputReader?.Dispose());
        Release(() => standardErrorReader?.Dispose());
        try { await control.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { RecordCleanupFailure(exception); }
        try { outputCancellation?.Dispose(); }
        catch (Exception exception) { RecordCleanupFailure(exception); }
        Release(() => job?.Dispose());
        Release(() => cgroup?.Remove());
        Release(Host.Dispose);
        disposed = true;
    }

    private void RecordCleanupFailure(Exception exception)
    {
        Interlocked.Exchange(ref cleanupFailed, 1);
        var primary = Interlocked.CompareExchange(ref primaryFailure, exception, null);
        if (primary is not null && !ContainsFailure(exception, primary))
        {
            RecordSecondaryFailure(exception);
        }
    }

    // Evidence persistence is a caller service. A late recorder failure still
    // enters the same causal outcome without replacing an observed live process.
    internal ProcessLifecycleOutcome RecordEvidenceFailure(
        ProcessLifecycleOutcome completed, Exception failure)
    {
        RecordCleanupFailure(failure);
        var amended = completed with
        {
            CleanupSucceeded = false,
            PrimaryFailure = PrimaryFailure,
            SecondaryCleanupFailure = SecondaryCleanupFailure
        };
        completionTask = Task.FromResult(amended);
        return amended;
    }

    private void RecordSecondaryFailure(Exception exception)
    {
        lock (secondaryFailureGate)
        {
            if (SecondaryCleanupFailure is not null &&
                (ContainsFailure(SecondaryCleanupFailure, exception) ||
                 ContainsFailure(exception, SecondaryCleanupFailure)))
            {
                return;
            }
            SecondaryCleanupFailure = SecondaryCleanupFailure is null ? exception :
                new AggregateException(SecondaryCleanupFailure, exception);
        }
    }

    private static bool ContainsFailure(Exception candidate, Exception expected) =>
        ReferenceEquals(candidate, expected) ||
        candidate is AggregateException aggregate &&
        aggregate.InnerExceptions.Any(inner => ContainsFailure(inner, expected));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Containment confirmation failure must not skip mandatory helper reaping.")]
    private async Task TerminateAsync(
        CleanupDeadline deadline, Func<int, string?>? unixGroupObserver = null)
    {
        Exception? containmentFailure = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                TerminateWindowsJob(job!);
                await WaitForWindowsJobStoppedAsync(deadline).ConfigureAwait(false);
            }
            else if (cgroup is not null)
            {
                cgroup.Kill();
                await WaitForCgroupStoppedAsync(deadline).ConfigureAwait(false);
            }
            else
            {
                // Reap Host only after this signal and group confirmation;
                // its unreaped leader reserves the process-group identity.
                if (NativeMethods.KillProcessGroup(Host.Id, SigKill) != 0 &&
                    Marshal.GetLastPInvokeError() != NoSuchProcess)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                await WaitForGroupStoppedAsync(deadline, unixGroupObserver).ConfigureAwait(false);
            }

        }
        catch (Exception exception)
        {
            containmentFailure = exception;
            RecordCleanupFailure(exception);
        }

        // Confirmation can fail after a live observation. It must never skip
        // reaping the host that anchors the authoritative containment.
        Exception? reapFailure = null;
        try
        {
            using var reapCancellation = new CancellationTokenSource(deadline.WorkWindow);
            await Host.WaitForExitAsync(reapCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            reapFailure = new TimeoutException(
                "The ownership host was not reaped by the cleanup deadline.", exception);
        }
        catch (Exception exception)
        {
            reapFailure = exception;
        }

        if (reapFailure is not null)
        {
            RecordCleanupFailure(reapFailure);
            if (!Host.HasExited)
            {
                Host.Kill();
                using var forcedReapCancellation =
                    new CancellationTokenSource(deadline.Remaining);
                try
                {
                    await Host.WaitForExitAsync(forcedReapCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (forcedReapCancellation.IsCancellationRequested)
                {
                    // A kernel-delayed reap cannot satisfy both a hard return
                    // bound and the no-owned-helper-after-return invariant.
                    // This remains a failed cleanup; never abandon the helper.
                    await Host.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }

        if (containmentFailure is not null && reapFailure is not null)
        {
            throw new AggregateException(containmentFailure, reapFailure);
        }
        if (containmentFailure is not null)
        {
            throw containmentFailure;
        }
        if (reapFailure is not null)
        {
            throw reapFailure;
        }
    }

    internal async Task WaitForGroupStoppedAsync(
        CleanupDeadline deadline, Func<int, string?>? observe = null)
    {
        string? liveMember = null;
        observe ??= groupId => UnixProcessGroupInspector.ReadLiveMember(
            groupId, deadline: deadline);
        while (deadline.WorkWindow > TimeSpan.Zero)
        {
            try
            {
                liveMember = observe(Host.Id);
            }
            catch (Exception exception) when (liveMember is not null)
            {
                LiveEvidence = liveMember;
                var liveFailure = new TimeoutException(
                    $"Owned process group {Host.Id} could not be confirmed stopped; last observed {liveMember}.");
                RecordCleanupFailure(liveFailure);
                RecordSecondaryFailure(exception);
                throw new AggregateException(liveFailure, exception);
            }

            if (liveMember is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, deadline.WorkWindow.Ticks))).ConfigureAwait(false);
        }

        LiveEvidence = liveMember;
        var deadlineFailure = new TimeoutException(
            $"Owned process group {Host.Id} could not be confirmed stopped by the cleanup deadline; last observed {liveMember}.");
        RecordCleanupFailure(deadlineFailure);
        throw deadlineFailure;
    }

    internal async Task WaitForCgroupStoppedAsync(
        CleanupDeadline deadline,
        Func<bool>? isQuiescent = null,
        Func<CleanupDeadline, string?>? readLiveEvidence = null)
    {
        isQuiescent ??= cgroup!.IsQuiescent;
        readLiveEvidence ??= cgroup!.ReadLiveEvidence;
        string? lastLive = null;
        while (deadline.WorkWindow > TimeSpan.Zero)
        {
            try
            {
                if (isQuiescent())
                {
                    return;
                }

                lastLive = "populated=1";
                lastLive = readLiveEvidence(deadline) ?? lastLive;
            }
            catch (Exception exception) when (lastLive is not null)
            {
                LiveEvidence = lastLive;
                var liveFailure = new TimeoutException(
                    $"Owned cgroup could not be confirmed stopped; last observed {lastLive}.");
                RecordCleanupFailure(liveFailure);
                RecordSecondaryFailure(exception);
                throw new AggregateException(liveFailure, exception);
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, deadline.WorkWindow.Ticks))).ConfigureAwait(false);
        }

        LiveEvidence = lastLive;
        var deadlineFailure = new TimeoutException(
            $"The owned cgroup remained populated at the cleanup deadline; last observed {lastLive}.");
        RecordCleanupFailure(deadlineFailure);
        throw deadlineFailure;
    }

    internal async Task WaitForWindowsJobStoppedAsync(
        CleanupDeadline deadline, Func<uint>? activeProcessCount = null)
    {
        activeProcessCount ??= () => GetWindowsJobActiveProcessCount(job!);
        uint? lastActiveCount = null;
        while (deadline.WorkWindow > TimeSpan.Zero)
        {
            try
            {
                var activeCount = activeProcessCount();
                if (activeCount == 0)
                {
                    return;
                }

                lastActiveCount = activeCount;
            }
            catch (Exception exception) when (lastActiveCount is not null)
            {
                LiveEvidence = $"active processes={lastActiveCount}";
                var liveFailure = new TimeoutException(
                    $"Owned Windows Job could not be confirmed stopped; last observed {LiveEvidence}.");
                RecordCleanupFailure(liveFailure);
                RecordSecondaryFailure(exception);
                throw new AggregateException(liveFailure, exception);
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, deadline.WorkWindow.Ticks))).ConfigureAwait(false);
        }

        LiveEvidence = lastActiveCount is null ? null : $"active processes={lastActiveCount}";
        var deadlineFailure = new TimeoutException(
            $"The owned Windows Job remained active at the cleanup deadline; last observed {LiveEvidence}.");
        RecordCleanupFailure(deadlineFailure);
        throw deadlineFailure;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        var hadCompletion = completionTask is not null;
        var outcome = await (completionTask ?? CompleteAsync(TimeSpan.Zero, CancellationToken.None))
            .ConfigureAwait(false);
        if (!hadCompletion && !outcome.CleanupSucceeded)
        {
            throw outcome.PrimaryFailure ??
                new InvalidOperationException("Emergency lifecycle cleanup did not complete.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Any host failure after launch must retain the Unix group anchor until owner cleanup.")]
    [SuppressMessage("Reliability", "CA2025:Do not pass an IDisposable instance into an unawaited Task",
        Justification = "Normal relay tasks are joined; on host failure the owner terminates and reaps the process before returning.")]
    internal static async Task<int> RunHostAsync(string pipeName, string jobName)
    {
        using var control = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await control.ConnectAsync().ConfigureAwait(false);
        using var writer = new StreamWriter(control) { AutoFlush = true };
        Process? child = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var openedJob = NativeMethods.OpenJobObject(0x001F003F, false, jobName);
                if (openedJob.IsInvalid || !NativeMethods.AssignProcessToJobObject(
                        openedJob, NativeMethods.GetCurrentProcess()))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            else if (NativeMethods.CreateSession() < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            await writer.WriteLineAsync("ready").ConfigureAwait(false);
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The scoped launch request was missing.");
            var launch = JsonSerializer.Deserialize<ScopeLaunch>(line)
                ?? throw new InvalidOperationException("The scoped launch request was invalid.");
            if ((launch.OutputPipeName is null) != (launch.ErrorPipeName is null))
            {
                throw new InvalidOperationException("Both output relay channels are required.");
            }

            using var outputChannel = launch.OutputPipeName is null ? null :
                new NamedPipeClientStream(".", launch.OutputPipeName,
                    PipeDirection.Out, PipeOptions.Asynchronous);
            using var errorChannel = launch.ErrorPipeName is null ? null :
                new NamedPipeClientStream(".", launch.ErrorPipeName,
                    PipeDirection.Out, PipeOptions.Asynchronous);
            if (outputChannel is not null)
            {
                await outputChannel.ConnectAsync().ConfigureAwait(false);
                await errorChannel!.ConnectAsync().ConfigureAwait(false);
            }

            var childInfo = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                WorkingDirectory = launch.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = outputChannel is not null,
                RedirectStandardError = errorChannel is not null
            };
            foreach (var argument in launch.Arguments)
            {
                childInfo.ArgumentList.Add(argument);
            }
            childInfo.Environment.Clear();
            foreach (var pair in launch.Environment)
            {
                childInfo.Environment[pair.Key] = pair.Value;
            }

            child = Process.Start(childInfo)
                ?? throw new InvalidOperationException("The scoped test process did not start.");
            child.StandardInput.Close(); // Workload stdin must not consume the host's lifecycle channel.
            var outputRelay = outputChannel is null ? Task.CompletedTask :
                RelayOutputAsync(child.StandardOutput.BaseStream, outputChannel);
            var errorRelay = errorChannel is null ? Task.CompletedTask :
                RelayOutputAsync(child.StandardError.BaseStream, errorChannel);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ScopeHandshake(
                child.Id, child.StartTime.ToUniversalTime(), null))).ConfigureAwait(false);
            var childExit = child.WaitForExitAsync();
            if (outputChannel is not null)
            {
                var pending = new List<Task> { childExit, outputRelay, errorRelay };
                while (!childExit.IsCompleted)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    if (completed == childExit)
                    {
                        break;
                    }
                    pending.Remove(completed);
                    if (completed.IsFaulted || completed.IsCanceled)
                    {
                        await writer.WriteLineAsync("output_failed").ConfigureAwait(false);
                        // The owner will stop the live root; leaving now would
                        // release the authoritative Unix group identity.
                        await Console.In.ReadLineAsync().ConfigureAwait(false);
                        return 2;
                    }
                }
            }
            await childExit.ConfigureAwait(false);
            await writer.WriteLineAsync(child.ExitCode.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            if (outputChannel is not null)
            {
                try
                {
                    await Task.WhenAll(outputRelay, errorRelay).ConfigureAwait(false);
                    await writer.WriteLineAsync("output_complete").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await writer.WriteLineAsync("output_failed").ConfigureAwait(false);
                }
            }
            if (!OperatingSystem.IsWindows())
            {
                // A live session leader reserves the group ID until the
                // lifecycle owner has signalled, confirmed and reaped it.
                await Console.In.ReadLineAsync().ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new ScopeHandshake(0, default, exception.Message))).ConfigureAwait(false);
            }
            catch (IOException) { }
            if (child is not null && !OperatingSystem.IsWindows())
            {
                await Console.In.ReadLineAsync().ConfigureAwait(false);
            }
            return 2;
        }
        finally
        {
            child?.Dispose();
        }
    }

    private static async Task RelayOutputAsync(Stream source, Stream destination)
    {
        try
        {
            await source.CopyToAsync(destination).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await destination.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SafeFileHandle CreateWindowsJob(string name)
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 }
        };
        if (!NativeMethods.SetInformationJobObject(handle, 9, in limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw error;
        }

        return handle;
    }

    private static void TerminateWindowsJob(SafeFileHandle handle)
    {
        if (!NativeMethods.TerminateJobObject(handle, 2))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static uint GetWindowsJobActiveProcessCount(SafeFileHandle handle)
    {
        if (!NativeMethods.QueryInformationJobObject(handle, 1,
                out JobObjectBasicAccountingInformation information,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return information.ActiveProcesses;
    }

    private sealed record ScopeLaunch(
        string FileName,
        string[] Arguments,
        string WorkingDirectory,
        Dictionary<string, string?> Environment,
        string? OutputPipeName,
        string? ErrorPipeName);

    private sealed record ScopeHandshake(int Pid, DateTimeOffset StartTimeUtc, string? Error);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int CreateSession();

        [DllImport("libc", EntryPoint = "killpg", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int KillProcessGroup(int groupId, int signal);

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle OpenJobObject(uint access, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle handle, int informationClass,
            in JobObjectExtendedLimitInformation information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            SafeFileHandle job, int informationClass,
            out JobObjectBasicAccountingInformation information, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentProcess();
    }
}
