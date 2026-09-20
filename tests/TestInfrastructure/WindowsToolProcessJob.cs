using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.TestInfrastructure;

internal sealed class WindowsToolProcessJob : IDisposable
{
    private const uint ActiveProcessZero = 4;
    private const uint KillOnJobClose = 0x00002000;
    private const int WaitTimeout = 258;
    private readonly SafeFileHandle completionPort;
    private readonly SafeFileHandle handle;

    private WindowsToolProcessJob(SafeFileHandle handle, SafeFileHandle completionPort)
    {
        this.handle = handle;
        this.completionPort = completionPort;
    }

    internal static WindowsToolProcessJob Create()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = KillOnJobClose
            }
        };
        if (!NativeMethods.SetInformationJobObject(
                handle,
                9,
                in limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw failure;
        }

        var completionPort = NativeMethods.CreateIoCompletionPort(
            new IntPtr(-1),
            IntPtr.Zero,
            UIntPtr.Zero,
            1);
        if (completionPort.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw failure;
        }

        var association = new JobObjectAssociateCompletionPort
        {
            CompletionKey = new IntPtr(1),
            CompletionPort = completionPort.DangerousGetHandle()
        };
        if (!NativeMethods.SetCompletionPort(
                handle,
                7,
                in association,
                (uint)Marshal.SizeOf<JobObjectAssociateCompletionPort>()))
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            completionPort.Dispose();
            handle.Dispose();
            throw failure;
        }

        return new WindowsToolProcessJob(handle, completionPort);
    }

    internal void Assign(Process process)
    {
        if (!NativeMethods.AssignProcessToJobObject(handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal void Terminate()
    {
        if (!NativeMethods.TerminateJobObject(handle, 2))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal bool WaitForEmpty(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (ReadActiveProcessCount() == 0)
            {
                return true;
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return ReadActiveProcessCount() == 0;
            }

            var nativeWait = TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromMilliseconds(50).Ticks,
                remaining.Ticks));
            if (!NativeMethods.GetQueuedCompletionStatus(
                    completionPort,
                    out var message,
                    out _,
                    out _,
                    ToNativeTimeout(nativeWait)))
            {
                var errorCode = Marshal.GetLastPInvokeError();
                if (errorCode != WaitTimeout)
                {
                    throw new Win32Exception(errorCode);
                }
            }
            else if (message == ActiveProcessZero)
            {
                return ReadActiveProcessCount() == 0;
            }
        }
    }

    public void Dispose()
    {
        handle.Dispose();
        completionPort.Dispose();
    }

    private uint ReadActiveProcessCount()
    {
        if (!NativeMethods.QueryInformationJobObject(
                handle,
                1,
                out JobObjectBasicAccountingInformation accounting,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return accounting.ActiveProcesses;
    }

    private static uint ToNativeTimeout(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero
            ? 0
            : (uint)Math.Min(Math.Ceiling(timeout.TotalMilliseconds), uint.MaxValue - 1d);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectAssociateCompletionPort
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            in JobObjectExtendedLimitInformation information,
            uint length);

        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCompletionPort(
            SafeFileHandle job,
            int informationClass,
            in JobObjectAssociateCompletionPort information,
            uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateIoCompletionPort(
            IntPtr fileHandle,
            IntPtr existingCompletionPort,
            UIntPtr completionKey,
            uint numberOfConcurrentThreads);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetQueuedCompletionStatus(
            SafeFileHandle completionPort,
            out uint numberOfBytesTransferred,
            out nuint completionKey,
            out IntPtr overlapped,
            uint milliseconds);

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
            SafeFileHandle job,
            int informationClass,
            out JobObjectBasicAccountingInformation information,
            uint length,
            IntPtr returnLength);
    }
}
