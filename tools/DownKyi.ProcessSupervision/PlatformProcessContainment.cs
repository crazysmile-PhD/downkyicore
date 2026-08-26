using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.ProcessSupervision;

internal interface IProcessContainmentLease : IDisposable
{
    ProcessOwnershipMetadata Metadata { get; }

    bool IsTreeQuiescent();

    void Terminate();
}

internal static class PlatformProcessContainment
{
    public static IProcessContainmentLease Create(
        Process supervisor,
        string windowsJobName,
        ProcessOwnershipMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        IProcessContainmentLease containment = OperatingSystem.IsWindows()
            ? WindowsJobContainmentLease.Create(supervisor, windowsJobName, mutation)
            : PosixProcessGroupContainmentLease.Create(supervisor, mutation);
        containment = mutation.HasFlag(ProcessOwnershipMutation.FailAfterContainmentTermination)
            ? new TerminationFailureMutationContainmentLease(containment)
            : containment;
        return mutation.HasFlag(ProcessOwnershipMutation.ReportTreeQuiescentOnce)
            ? new TreeQuiescenceMutationContainmentLease(containment)
            : containment;
    }

    public static bool EstablishCurrentProcessOwnership(
        string windowsJobName,
        ProcessOwnershipMutation mutation)
    {
        if (mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsJobContainmentLease.IsCurrentProcessInJob(windowsJobName);
        }

        if (PosixNative.SetProcessGroup(0, 0) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The supervisor could not establish its POSIX process group.");
        }

        return PosixNative.GetProcessGroup() == Environment.ProcessId;
    }

    public static bool IsCurrentTargetOwned(string containmentId)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsJobContainmentLease.IsCurrentProcessInJob(containmentId);
        }

        return int.TryParse(
                   containmentId,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var processGroupId) &&
               PosixNative.GetProcessGroup() == processGroupId;
    }
}

internal sealed partial class WindowsJobContainmentLease : IProcessContainmentLease
{
    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly SafeJobHandle _job;

    private WindowsJobContainmentLease(
        SafeJobHandle job,
        ProcessOwnershipMetadata metadata)
    {
        _job = job;
        Metadata = metadata;
    }

    public ProcessOwnershipMetadata Metadata { get; }

    public static WindowsJobContainmentLease Create(
        Process supervisor,
        string jobName,
        ProcessOwnershipMutation mutation)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var ownerWasAlreadyContained = IsProcessInAnyJob(currentProcess);
        SafeJobHandle? job = WindowsNative.CreateJobObject(IntPtr.Zero, jobName);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!WindowsNative.SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var ownershipEstablished =
                !mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership) &&
                WindowsNative.AssignProcessToJobObject(job, supervisor.Handle);
            if (mutation == ProcessOwnershipMutation.None && !ownershipEstablished)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "The supervisor could not join its owned Job Object.");
            }

            var lease = new WindowsJobContainmentLease(
                job,
                new ProcessOwnershipMetadata(
                    ProcessIdentityAuthority.WindowsProcessHandle,
                    ProcessContainmentKind.WindowsJobObject,
                    ProcessContainmentStrength.KernelJobTree,
                    jobName,
                    ownershipEstablished,
                    ownerWasAlreadyContained));
            job = null;
            return lease;
        }
        finally
        {
            job?.Dispose();
        }
    }

    public static bool IsCurrentProcessInJob(string jobName)
    {
        using var job = WindowsNative.OpenJobObject(
            JobObjectQuery,
            inheritHandle: false,
            jobName);
        if (job.IsInvalid)
        {
            return false;
        }

        using var process = Process.GetCurrentProcess();
        return WindowsNative.IsProcessInJob(
                   process.Handle,
                   job.DangerousGetHandle(),
                   out var inJob) &&
               inJob;
    }

    public bool IsTreeQuiescent()
    {
        if (!WindowsNative.QueryInformationJobObject(
                _job,
                JobObjectBasicAccountingInformationClass,
                out JobObjectBasicAccountingInformation information,
                Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return information.ActiveProcesses == 0;
    }

    public void Terminate()
    {
        if (!WindowsNative.TerminateJobObject(_job, 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
        _job.Dispose();
    }

    private static bool IsProcessInAnyJob(Process process)
    {
        return WindowsNative.IsProcessInJob(
                   process.Handle,
                   IntPtr.Zero,
                   out var inJob) &&
               inJob;
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
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static partial class WindowsNative
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial SafeJobHandle CreateJobObject(
            IntPtr jobAttributes,
            string name);

        [LibraryImport("kernel32.dll", EntryPoint = "OpenJobObjectW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial SafeJobHandle OpenJobObject(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            string name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            int informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(
            SafeJobHandle job,
            IntPtr process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsProcessInJob(
            IntPtr process,
            IntPtr job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool QueryInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            out JobObjectBasicAccountingInformation information,
            int informationLength,
            out int returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr handle);
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return WindowsNative.CloseHandle(handle);
        }
    }
}

internal sealed class PosixProcessGroupContainmentLease : IProcessContainmentLease
{
    private const int NoSignal = 0;
    private const int KillSignal = 9;
    private const int OperationNotPermitted = 1;
    private const int NoSuchProcess = 3;

    private readonly int _processGroupId;
    private readonly bool _ownershipEstablished;

    private PosixProcessGroupContainmentLease(ProcessOwnershipMetadata metadata)
    {
        Metadata = metadata;
        _processGroupId = int.Parse(
            metadata.ContainmentId,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture);
        _ownershipEstablished = metadata.OwnershipEstablished;
    }

    public ProcessOwnershipMetadata Metadata { get; }

    public static PosixProcessGroupContainmentLease Create(
        Process supervisor,
        ProcessOwnershipMutation mutation)
    {
        return new PosixProcessGroupContainmentLease(
            new ProcessOwnershipMetadata(
                ProcessIdentityAuthority.DirectChildWait,
                ProcessContainmentKind.PosixProcessGroup,
                ProcessContainmentStrength.TrustedChildProcessGroup,
                supervisor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                !mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership),
                OwnerWasAlreadyContained: false));
    }

    public bool IsTreeQuiescent()
    {
        if (!_ownershipEstablished)
        {
            return true;
        }

        var result = PosixNative.SignalProcessGroup(_processGroupId, NoSignal);
        return InterpretQuiescenceProbe(result, Marshal.GetLastPInvokeError());
    }

    public void Terminate()
    {
        if (!_ownershipEstablished)
        {
            return;
        }

        if (PosixNative.SignalProcessGroup(_processGroupId, KillSignal) != 0 &&
            Marshal.GetLastPInvokeError() != NoSuchProcess)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
    }

    internal static bool InterpretQuiescenceProbe(int result, int error)
    {
        if (result == 0)
        {
            return false;
        }

        return error switch
        {
            NoSuchProcess => true,
            OperationNotPermitted => false,
            _ => throw new Win32Exception(error)
        };
    }
}

internal sealed class TerminationFailureMutationContainmentLease : IProcessContainmentLease
{
    private readonly IProcessContainmentLease _inner;

    public TerminationFailureMutationContainmentLease(IProcessContainmentLease inner)
    {
        _inner = inner;
    }

    public ProcessOwnershipMetadata Metadata => _inner.Metadata;

    public bool IsTreeQuiescent()
    {
        return _inner.IsTreeQuiescent();
    }

    public void Terminate()
    {
        _inner.Terminate();
        throw new InvalidOperationException("Injected containment termination failure.");
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

internal sealed class TreeQuiescenceMutationContainmentLease : IProcessContainmentLease
{
    private readonly IProcessContainmentLease _inner;
    private int _mutationApplied;

    public TreeQuiescenceMutationContainmentLease(IProcessContainmentLease inner)
    {
        _inner = inner;
    }

    public ProcessOwnershipMetadata Metadata => _inner.Metadata;

    public bool IsTreeQuiescent()
    {
        return Interlocked.Exchange(ref _mutationApplied, 1) == 0 ||
               _inner.IsTreeQuiescent();
    }

    public void Terminate()
    {
        _inner.Terminate();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

internal static partial class PosixNative
{
    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int SetProcessGroup(int processId, int processGroupId);

    [LibraryImport("libc", EntryPoint = "getpgrp", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int GetProcessGroup();

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int GetProcessGroup(int processId);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int processId, int signal);

    public static int SignalProcessGroup(int processGroupId, int signal)
    {
        return Kill(-processGroupId, signal);
    }
}
