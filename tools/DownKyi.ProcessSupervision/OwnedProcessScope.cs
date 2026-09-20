using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.ProcessSupervision;

// One test invocation owns one OS group. The small child host joins that group
// before it launches the test, so no test code can run outside the scope.
internal sealed class OwnedProcessScope : IDisposable
{
    internal const string FailedHostPidDataKey = "DownKyi.ProcessSupervision.FailedHostPid";
    private const int SigKill = 9;
    private const int OperationNotPermitted = 1;
    private const int NoSuchProcess = 3;
    private readonly SafeFileHandle? job;
    private bool terminationAttempted;

    private enum ScopeLifecycleState
    {
        HostNotStarted,
        HostStarted,
        ScopeMayContainTarget
    }

    private OwnedProcessScope(Process host, SafeFileHandle? job, ScopeHandshake handshake)
    {
        Host = host;
        this.job = job;
        RootPid = handshake.Pid;
        RootStartTimeUtc = handshake.StartTimeUtc;
    }

    internal Process Host { get; }
    internal int RootPid { get; }
    internal DateTimeOffset? RootStartTimeUtc { get; }
    internal SafeFileHandle? WindowsJobHandle => job;

    internal static Task<OwnedProcessScope> StartAsync(
        ProcessStartInfo testStartInfo,
        TimeSpan startupWindow) =>
        StartAsync(
            testStartInfo,
            startupWindow,
            sharedStartupDeadline: null,
            hostJobNameOverride: null,
            hostExecutableOverride: null);

    internal static Task<OwnedProcessScope> StartAsync(
        ProcessStartInfo testStartInfo,
        CleanupDeadline startupDeadline) =>
        StartAsync(
            testStartInfo,
            startupWindow: default,
            sharedStartupDeadline: startupDeadline,
            hostJobNameOverride: null,
            hostExecutableOverride: null);

    internal static Task<OwnedProcessScope> StartAsync(
        ProcessStartInfo testStartInfo,
        TimeSpan startupWindow,
        string? hostJobNameOverride,
        string? hostExecutableOverride = null) =>
        StartAsync(
            testStartInfo,
            startupWindow,
            sharedStartupDeadline: null,
            hostJobNameOverride: hostJobNameOverride,
            hostExecutableOverride: hostExecutableOverride);

    private static async Task<OwnedProcessScope> StartAsync(
        ProcessStartInfo testStartInfo,
        TimeSpan startupWindow,
        CleanupDeadline? sharedStartupDeadline,
        string? hostJobNameOverride,
        string? hostExecutableOverride)
    {
        TimeSpan CurrentStartupWindow() =>
            sharedStartupDeadline?.WorkWindow ?? startupWindow;

        if (CurrentStartupWindow() == TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The process supervision startup deadline expired before launch.");
        }

        // Unix named pipes include the temporary directory in a short socket path.
        var pipeName = Guid.NewGuid().ToString("N");
        using var control = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var jobName = OperatingSystem.IsWindows() ? $"Local\\downkyi-test-{Guid.NewGuid():N}" : null;
        SafeFileHandle? job = null;
        Process? host = null;
        var lifecycleState = ScopeLifecycleState.HostNotStarted;
        try
        {
            if (jobName is not null)
            {
                job = CreateWindowsJob(jobName);
            }

            var hostInfo = new ProcessStartInfo(hostExecutableOverride ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            hostInfo.ArgumentList.Add(typeof(ProcessSupervisionHost).Assembly.Location);
            hostInfo.ArgumentList.Add("owned-scope-host");
            hostInfo.ArgumentList.Add(pipeName);
            hostInfo.ArgumentList.Add(hostJobNameOverride ?? jobName ?? "-");
            host = new Process { StartInfo = hostInfo };
            if (!host.Start())
            {
                throw new InvalidOperationException("The ownership host did not start.");
            }
            lifecycleState = ScopeLifecycleState.HostStarted;

            var launch = new ScopeLaunch(
                testStartInfo.FileName,
                [.. testStartInfo.ArgumentList],
                testStartInfo.WorkingDirectory,
                new Dictionary<string, string?>(testStartInfo.Environment));
            var serializedLaunch = JsonSerializer.Serialize(launch);

            var launchWindow = CurrentStartupWindow();
            if (launchWindow == TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "The process supervision startup deadline expired before launch.");
            }

            // A timed-out write may still have delivered the request. From this
            // point forward, cleanup must assume that the OS scope has a target.
            lifecycleState = ScopeLifecycleState.ScopeMayContainTarget;
            await host.StandardInput.WriteLineAsync(serializedLaunch)
                .WaitAsync(launchWindow).ConfigureAwait(false);
            host.StandardInput.Close();
            await control.WaitForConnectionAsync()
                .WaitAsync(CurrentStartupWindow()).ConfigureAwait(false);
            using var reader = new StreamReader(control);
            var line = await reader.ReadLineAsync()
                .WaitAsync(CurrentStartupWindow()).ConfigureAwait(false);
            var handshake = line is null ? null : JsonSerializer.Deserialize<ScopeHandshake>(line);
            if (handshake is null || handshake.Error is not null || handshake.Pid <= 0)
            {
                throw new InvalidOperationException($"The ownership scope did not launch the test: {handshake?.Error ?? "no handshake"}");
            }

            var scope = new OwnedProcessScope(host, job, handshake);
            host = null;
            job = null;
            return scope;
        }
        catch (Exception primaryFailure)
        {
            if (lifecycleState is not ScopeLifecycleState.HostNotStarted && host is not null)
            {
                primaryFailure.Data[FailedHostPidDataKey] = host.Id;
            }

            var cleanupDeadline = sharedStartupDeadline ?? new CleanupDeadline(startupWindow);
            await TerminateAndReapAsync(
                    lifecycleState,
                    host,
                    job,
                    cleanupDeadline,
                    primaryFailure)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            host?.Dispose();
            job?.Dispose();
        }
    }

    private static async Task TerminateAndReapAsync(
        ScopeLifecycleState lifecycleState,
        Process? host,
        SafeFileHandle? job,
        CleanupDeadline deadline,
        Exception? primaryFailure)
    {
        var failures = new List<Exception>();
        if (lifecycleState is ScopeLifecycleState.HostNotStarted)
        {
            ThrowPreservingPrimaryFailure(primaryFailure, failures);
            return;
        }

        if (host is null)
        {
            failures.Add(new InvalidOperationException(
                $"Process supervision entered {lifecycleState} without an ownership host."));
            ThrowPreservingPrimaryFailure(primaryFailure, failures);
            return;
        }

        var entireScopeTerminationSucceeded = false;
        if (lifecycleState is ScopeLifecycleState.ScopeMayContainTarget)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (job is null)
                    {
                        throw new InvalidOperationException(
                            "The Windows process scope has no Job handle.");
                    }

                    TerminateWindowsJob(job);
                    entireScopeTerminationSucceeded = true;
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    entireScopeTerminationSucceeded =
                        SignalUnixProcessGroupIfPresent(
                            host.Id,
                            SigKill,
                            darwinMembershipAuthority: OperatingSystem.IsMacOS());
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            // Before the request, only the host exists. Once a request may have
            // arrived, a failed scope termination falls back to the host tree.
            // A successful Job termination can leave only a not-yet-joined host;
            // that host cannot have launched a target under the host protocol.
            if (!entireScopeTerminationSucceeded)
            {
                KillIfRunning(
                    host,
                    entireProcessTree:
                        lifecycleState is ScopeLifecycleState.ScopeMayContainTarget);
            }
            else if (OperatingSystem.IsWindows())
            {
                KillIfRunning(host, entireProcessTree: false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            failures.Add(exception);
        }

        // Unix group membership retains a dead host until its parent reaps it,
        // so waitpid must precede the group drain there. The drain repeats SIGKILL
        // because a failed-start host may complete setsid after the first probe.
        // Windows Job accounting has no corresponding zombie state and remains
        // scope-first.
        var reapHostBeforeScope =
            lifecycleState is ScopeLifecycleState.ScopeMayContainTarget &&
            (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        if (reapHostBeforeScope)
        {
            await ReapHostAsync(host, deadline.WorkWindow, failures).ConfigureAwait(false);
        }

        if (lifecycleState is ScopeLifecycleState.ScopeMayContainTarget)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (job is null)
                    {
                        throw new InvalidOperationException(
                            "The Windows process scope has no Job handle.");
                    }

                    await WaitForWindowsJobToEmptyAsync(job, deadline).ConfigureAwait(false);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    await WaitForMacProcessGroupToEmptyAsync(
                        host.Id, deadline, terminateMembers: true).ConfigureAwait(false);
                }
                else if (OperatingSystem.IsLinux())
                {
                    await WaitForLinuxProcessGroupToEmptyAsync(
                        host.Id, deadline, terminateMembers: true).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or TimeoutException)
            {
                failures.Add(exception);
            }
        }

        if (!reapHostBeforeScope)
        {
            await ReapHostAsync(host, deadline.Remaining, failures).ConfigureAwait(false);
        }

        ThrowPreservingPrimaryFailure(primaryFailure, failures);
    }

    private static async Task ReapHostAsync(
        Process host,
        TimeSpan window,
        List<Exception> failures)
    {
        try
        {
            using var hostExit = new CancellationTokenSource(window);
            await host.WaitForExitAsync(hostExit.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or OperationCanceledException)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowPreservingPrimaryFailure(
        Exception? primaryFailure,
        List<Exception> cleanupFailures)
    {
        var cleanupFailure = cleanupFailures.Count switch
        {
            0 => null,
            1 => cleanupFailures[0],
            _ => new AggregateException(
                "Process scope termination and reap had multiple failures.",
                cleanupFailures)
        };

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new InvalidOperationException(
                "Process supervision startup and cleanup both failed.",
                new AggregateException(primaryFailure, cleanupFailure));
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static void KillIfRunning(Process process, bool entireProcessTree)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Exiting between the state check and Kill already satisfies this step.
        }
    }

    internal async Task TerminateAsync(CleanupDeadline deadline)
    {
        terminationAttempted = true;
        await TerminateAndReapAsync(
                ScopeLifecycleState.ScopeMayContainTarget,
                Host,
                job,
                deadline,
                primaryFailure: null)
            .ConfigureAwait(false);
    }

    internal Task WaitForMacProcessGroupToEmptyAsync(CleanupDeadline deadline) =>
        WaitForMacProcessGroupToEmptyAsync(Host.Id, deadline, terminateMembers: false);

    private static async Task WaitForMacProcessGroupToEmptyAsync(
        int groupId,
        CleanupDeadline deadline,
        bool terminateMembers)
    {
        while (MacProcessGroupHasMembers(groupId))
        {
            if (terminateMembers)
            {
                _ = SignalUnixProcessGroupIfPresent(
                    groupId, SigKill, darwinMembershipAuthority: true);
            }

            var remaining = deadline.WorkWindow;
            if (remaining == TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The owned macOS process group {groupId} is still active.");
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, remaining.Ticks))).ConfigureAwait(false);
        }
    }

    private static bool MacProcessGroupHasMembers(int groupId)
    {
        // Darwin killpg skips zombies, so signal 0 can return EPERM for a group
        // that only contains zombies. libproc counts those members directly.
        var member = new int[1];
        var count = NativeMethods.ListProcessGroupPids(groupId, member, sizeof(int));
        return count >= 0
            ? count > 0
            : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal Task WaitForLinuxProcessGroupToEmptyAsync(CleanupDeadline deadline) =>
        WaitForLinuxProcessGroupToEmptyAsync(Host.Id, deadline, terminateMembers: false);

    private static async Task WaitForLinuxProcessGroupToEmptyAsync(
        int groupId,
        CleanupDeadline deadline,
        bool terminateMembers)
    {
        while (true)
        {
            if (!SignalUnixProcessGroupIfPresent(groupId, terminateMembers ? SigKill : 0))
            {
                return;
            }

            var remaining = deadline.WorkWindow;
            if (remaining == TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The owned Linux process group {groupId} is still active.");
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, remaining.Ticks))).ConfigureAwait(false);
        }
    }

    private static bool SignalUnixProcessGroupIfPresent(
        int groupId,
        int signal,
        bool darwinMembershipAuthority = false)
    {
        if (NativeMethods.KillProcessGroup(groupId, signal) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == NoSuchProcess)
        {
            return false;
        }

        // Darwin can report EPERM after every signalable member has exited.
        // The following libproc drain remains authoritative for membership.
        if (darwinMembershipAuthority && error == OperationNotPermitted)
        {
            return true;
        }

        throw new Win32Exception(error);
    }

    public void Dispose()
    {
        if (!terminationAttempted && !OperatingSystem.IsWindows())
        {
            // Emergency release when a caller faults before the bounded cleanup path.
            _ = NativeMethods.KillProcessGroup(Host.Id, SigKill);
        }

        job?.Dispose();
        Host.Dispose();
    }

    internal static async Task<int> RunHostAsync(string pipeName, string jobName)
    {
        using var control = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await control.ConnectAsync().ConfigureAwait(false);
        using var writer = new StreamWriter(control) { AutoFlush = true };
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The scoped launch request was missing.");
            var launch = JsonSerializer.Deserialize<ScopeLaunch>(line)
                ?? throw new InvalidOperationException("The scoped launch request was invalid.");

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

            var childInfo = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                WorkingDirectory = launch.WorkingDirectory,
                RedirectStandardOutput = false,
                RedirectStandardError = false
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

            using var child = Process.Start(childInfo)
                ?? throw new InvalidOperationException("The scoped test process did not start.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ScopeHandshake(
                child.Id, ReadStartTimeUtcBestEffort(child), null))).ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
            return child.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ScopeHandshake(0, default, exception.Message)))
                .ConfigureAwait(false);
            return 2;
        }
    }

    internal static DateTimeOffset? ReadStartTimeUtcBestEffort(
        Process process,
        Func<Process, DateTimeOffset>? readStartTimeUtc = null)
    {
        try
        {
            return (readStartTimeUtc ?? ReadStartTimeUtc)(process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadStartTimeUtc(Process process) =>
        process.StartTime.ToUniversalTime();

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

    private static async Task WaitForWindowsJobToEmptyAsync(
        SafeFileHandle handle, CleanupDeadline deadline)
    {
        while (true)
        {
            if (!NativeMethods.QueryInformationJobObject(handle, 1,
                    out JobObjectBasicAccountingInformation accounting,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (accounting.ActiveProcesses == 0)
            {
                return;
            }

            // The host can join an initially empty Job after the first
            // termination request. Reapply termination to any later members.
            TerminateWindowsJob(handle);

            var remaining = deadline.WorkWindow;
            if (remaining == TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The owned Windows Job still has {accounting.ActiveProcesses} active processes.");
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, remaining.Ticks))).ConfigureAwait(false);
        }
    }

    private sealed record ScopeLaunch(
        string FileName,
        string[] Arguments,
        string WorkingDirectory,
        Dictionary<string, string?> Environment);

    private sealed record ScopeHandshake(int Pid, DateTimeOffset? StartTimeUtc, string? Error);

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

        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listpgrppids", SetLastError = true)]
        internal static extern int ListProcessGroupPids(int groupId, [Out] int[] buffer, int size);

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
