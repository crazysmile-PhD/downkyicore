using System.ComponentModel;
using System.Diagnostics;
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
    private bool terminationAttempted;

    private OwnedProcessScope(Process host, SafeFileHandle? job, ScopeHandshake handshake)
    {
        Host = host;
        this.job = job;
        RootPid = handshake.Pid;
        RootStartTimeUtc = handshake.StartTimeUtc;
    }

    internal Process Host { get; }
    internal int RootPid { get; }
    internal DateTimeOffset RootStartTimeUtc { get; }
    internal SafeFileHandle? WindowsJobHandle => job;

    internal static async Task<OwnedProcessScope> StartAsync(ProcessStartInfo testStartInfo, TimeSpan startupWindow)
    {
        // Unix named pipes include the temporary directory in a short socket path.
        var pipeName = Guid.NewGuid().ToString("N");
        using var control = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var jobName = OperatingSystem.IsWindows() ? $"Local\\downkyi-test-{Guid.NewGuid():N}" : null;
        SafeFileHandle? job = null;
        Process? host = null;
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
            using var reader = new StreamReader(control);
            var line = await reader.ReadLineAsync().WaitAsync(startupWindow).ConfigureAwait(false);
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
            host?.Dispose();
            job?.Dispose();
        }
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
            try
            {
                liveMember = await ReadLiveGroupMemberAsync(Host.Id, deadline).ConfigureAwait(false);
            }
            catch (TimeoutException exception) when (liveMember is not null)
            {
                throw new TimeoutException(
                    $"Owned process group {Host.Id} could not be confirmed stopped; last observed {liveMember}.",
                    exception);
            }
            if (liveMember is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(10).Ticks, deadline.WorkWindow.Ticks))).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Owned process group {Host.Id} could not be confirmed stopped by the cleanup deadline; last observed {liveMember}.");
    }

    private static async Task<string?> ReadLiveGroupMemberAsync(int groupId, CleanupDeadline deadline)
    {
        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,pgid=,stat=");
        using var ps = new Process { StartInfo = startInfo };
        ps.Start();
        using var inspectionCancellation = new CancellationTokenSource();
        var outputTask = ps.StandardOutput.ReadToEndAsync(inspectionCancellation.Token);
        var errorTask = ps.StandardError.ReadToEndAsync(inspectionCancellation.Token);
        var exitTask = ps.WaitForExitAsync(inspectionCancellation.Token);
        try
        {
            try
            {
                await exitTask.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
                var output = await outputTask.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
                var error = await errorTask.WaitAsync(deadline.WorkWindow).ConfigureAwait(false);
                if (ps.ExitCode != 0 || error.Length > 0)
                {
                    throw new InvalidOperationException($"Owned process group inspection failed: {error.Trim()}");
                }

                return FindLiveGroupMember(output, groupId);
            }
            finally
            {
                await inspectionCancellation.CancelAsync().ConfigureAwait(false);
                ps.StandardOutput.Dispose();
                ps.StandardError.Dispose();
                if (!ps.HasExited)
                {
                    try
                    {
                        ps.Kill();
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or Win32Exception && ps.HasExited)
                    {
                        // The inspection process exited between the liveness check and signal.
                    }
                }

                using var reapCancellation = new CancellationTokenSource(deadline.Remaining);
                try
                {
                    await ps.WaitForExitAsync(reapCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await Task.WhenAll(outputTask, errorTask, exitTask)
                            .WaitAsync(deadline.Remaining).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        inspectionCancellation.IsCancellationRequested &&
                        exception is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                        // The inspector streams were closed after the bounded observation.
                    }
                }
            }
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("Owned process group inspection exceeded the bounded cleanup window.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Owned process group inspection I/O failed.", exception);
        }
    }

    internal static string? FindLiveGroupMember(string output, int groupId)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 ||
                !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processGroup) ||
                pid <= 0 || processGroup < 0)
            {
                throw new InvalidOperationException($"Invalid owned process group inspection row: {line}");
            }

            if (processGroup == groupId && fields[2][0] is not ('Z' or 'X'))
            {
                return $"pid={pid}, state={fields[2]}";
            }
        }

        return null;
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
                child.Id, child.StartTime.ToUniversalTime(), null))).ConfigureAwait(false);
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
