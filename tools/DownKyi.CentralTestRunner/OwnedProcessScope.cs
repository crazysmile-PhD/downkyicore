using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

// One test invocation owns one OS group. The small child host joins that group
// before it launches the test, so no test code can run outside the scope.
internal sealed class OwnedProcessScope : IDisposable
{
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;
    private readonly SafeFileHandle? job;
    private readonly StreamReader controlReader;
    private bool terminationAttempted;

    private OwnedProcessScope(Process host, SafeFileHandle? job, ScopeHandshake handshake,
        StreamReader controlReader)
    {
        Host = host;
        this.job = job;
        this.controlReader = controlReader;
        RootPid = handshake.Pid;
        RootStartTimeUtc = handshake.StartTimeUtc;
    }

    internal Process Host { get; }
    internal int RootPid { get; }
    internal DateTimeOffset RootStartTimeUtc { get; }
    internal SafeFileHandle? WindowsJobHandle => job;

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Startup failure also occurs before the job handle transfers to the scope.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The control reader transfers to the scope; failed launches dispose the pipe in finally.")]
    internal static async Task<OwnedProcessScope> StartAsync(ProcessStartInfo testStartInfo, TimeSpan startupWindow)
    {
        // Unix named pipes include the temporary directory in a short socket path.
        var pipeName = Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var jobName = OperatingSystem.IsWindows() ? $"Local\\downkyi-test-{Guid.NewGuid():N}" : null;
        SafeFileHandle? job = null;
        Process? host = null;
        StreamReader? reader = null;
        var controlTransferred = false;
        try
        {
            if (jobName is not null)
            {
                job = CreateWindowsJob(jobName);
            }

            var hostInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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

            var launch = new ScopeLaunch(
                testStartInfo.FileName,
                [.. testStartInfo.ArgumentList],
                testStartInfo.WorkingDirectory,
                new Dictionary<string, string?>(testStartInfo.Environment));
            await host.StandardInput.WriteLineAsync(JsonSerializer.Serialize(launch))
                .WaitAsync(startupWindow).ConfigureAwait(false);
            host.StandardInput.Close();
            await control.WaitForConnectionAsync().WaitAsync(startupWindow).ConfigureAwait(false);
            reader = new StreamReader(control);
            var line = await reader.ReadLineAsync().WaitAsync(startupWindow).ConfigureAwait(false);
            var handshake = line is null ? null : JsonSerializer.Deserialize<ScopeHandshake>(line);
            if (handshake is null || handshake.Error is not null || handshake.Pid <= 0)
            {
                throw new InvalidOperationException($"The ownership scope did not launch the test: {handshake?.Error ?? "no handshake"}");
            }

            var scope = new OwnedProcessScope(host, job, handshake, reader);
            controlTransferred = true;
            host = null;
            job = null;
            reader = null;
            return scope;
        }
        catch
        {
            if (job is not null)
            {
                TerminateWindowsJob(job);
            }
            else if (host is { HasExited: false })
            {
                // The host may already have created its group and launched the test.
                var groupTerminated = NativeMethods.KillProcessGroup(host.Id, SigKill) == 0;
                if (!groupTerminated && !host.HasExited)
                {
                    host.Kill();
                }
            }

            throw;
        }
        finally
        {
            reader?.Dispose();
            if (!controlTransferred)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            host?.Dispose();
            job?.Dispose();
        }
    }

    internal async Task<int> WaitForRootExitAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Host.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return Host.ExitCode;
        }

        var report = await controlReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(report, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var exitCode))
        {
            throw new InvalidOperationException("The ownership host did not report the root exit code.");
        }

        return exitCode;
    }

    internal async Task TerminateAsync(CleanupDeadline deadline)
    {
        terminationAttempted = true;
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() => TerminateWindowsJob(job!))
                .WaitAsync(deadline.Remaining).ConfigureAwait(false);
            return;
        }

        if (Host.HasExited)
        {
            throw new InvalidOperationException(
                "The ownership host exited before the process group could be signalled safely.");
        }

        if (NativeMethods.KillProcessGroup(Host.Id, SigKill) != 0 &&
            Marshal.GetLastPInvokeError() != NoSuchProcess)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        await WaitForGroupStoppedAsync(deadline).ConfigureAwait(false);
    }

    internal async Task WaitForGroupStoppedAsync(CleanupDeadline deadline)
    {
        string? liveMember = null;
        while (deadline.WorkWindow > TimeSpan.Zero)
        {
            liveMember = UnixProcessGroupInspector.ReadLiveMember(
                Host.Id, deadline, ignoredPid: Host.Id);
            if (liveMember is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, deadline.WorkWindow.Ticks))).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Owned process group {Host.Id} could not be confirmed stopped; last observed {liveMember ?? "unknown"}.");
    }

    public void Dispose()
    {
        if (!terminationAttempted && !OperatingSystem.IsWindows())
        {
            // Emergency release when a caller faults before the bounded cleanup path.
            _ = NativeMethods.KillProcessGroup(Host.Id, SigKill);
        }

        controlReader.Dispose();
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
            if (!OperatingSystem.IsWindows())
            {
                // The child inherited both output pipes. The host must release
                // its copies while it remains alive as the process-group anchor.
                var outputError = NativeMethods.Close(1) == 0 ? 0 : Marshal.GetLastPInvokeError();
                var errorError = NativeMethods.Close(2) == 0 ? 0 : Marshal.GetLastPInvokeError();
                if (outputError != 0 || errorError != 0)
                {
                    throw new Win32Exception(outputError != 0 ? outputError : errorError);
                }
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ScopeHandshake(
                child.Id, child.StartTime.ToUniversalTime(), null))).ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                return child.ExitCode;
            }

            await writer.WriteLineAsync(child.ExitCode.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ScopeHandshake(0, default, exception.Message)))
                .ConfigureAwait(false);
            return 2;
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

    private sealed record ScopeLaunch(
        string FileName,
        string[] Arguments,
        string WorkingDirectory,
        Dictionary<string, string?> Environment);

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

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int CreateSession();

        [DllImport("libc", EntryPoint = "killpg", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int KillProcessGroup(int groupId, int signal);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Close(int descriptor);

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

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentProcess();
    }
}
