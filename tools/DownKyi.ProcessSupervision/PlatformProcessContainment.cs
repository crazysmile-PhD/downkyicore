using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.ProcessSupervision;

internal interface IProcessContainmentLease : IDisposable
{
    ProcessOwnershipMetadata Metadata { get; }

    bool MembershipRequiresAnchorExit { get; }

    void Establish(Process supervisor, ProcessOwnershipMutation mutation);

    bool IsTreeQuiescent();

    void Terminate();

    void MarkAnchorReaped();
}

internal static class PlatformProcessContainment
{
    public static IProcessContainmentLease Prepare(
        Process supervisor,
        string windowsJobName)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        return OperatingSystem.IsWindows()
            ? WindowsJobContainmentLease.Prepare(windowsJobName)
            : OperatingSystem.IsLinux()
                ? LinuxCgroupContainmentLease.Prepare(supervisor)
                : OperatingSystem.IsMacOS()
                    ? MacProcessGroupContainmentLease.Prepare(supervisor)
                    : throw new PlatformNotSupportedException(
                        "Owned process membership is supported only on Windows, Linux, and macOS.");
    }

    public static IProcessContainmentLease ApplyFailureMutations(
        IProcessContainmentLease containment,
        ProcessOwnershipMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(containment);
        containment = mutation.HasFlag(ProcessOwnershipMutation.FailAfterContainmentTermination)
            ? new TerminationFailureMutationContainmentLease(containment)
            : containment;
        return mutation.HasFlag(ProcessOwnershipMutation.FailMembershipQuery)
            ? new MembershipFailureMutationContainmentLease(containment)
            : containment;
    }

    public static bool EstablishCurrentProcessOwnership(
        string containmentId,
        string membershipId,
        ProcessOwnershipMutation mutation)
    {
        if (mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership) ||
            mutation.HasFlag(ProcessOwnershipMutation.FailOwnershipEstablishment))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsJobContainmentLease.IsCurrentProcessInJob(containmentId);
        }

        if (PosixNative.SetProcessGroup(0, 0) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The supervisor could not establish its POSIX process group.");
        }

        if (PosixNative.GetProcessGroup() != Environment.ProcessId)
        {
            return false;
        }

        return OperatingSystem.IsLinux()
            ? LinuxCgroupContainmentLease.IsCurrentProcessInCgroup(membershipId)
            : OperatingSystem.IsMacOS() &&
              MacProcessGroupContainmentLease.ContainsProcess(
                  Environment.ProcessId,
                  Environment.ProcessId);
    }

    public static bool IsCurrentTargetOwned(
        string containmentId,
        string membershipId)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsJobContainmentLease.IsCurrentProcessInJob(containmentId);
        }

        if (!int.TryParse(
                containmentId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processGroupId) ||
            PosixNative.GetProcessGroup() != processGroupId)
        {
            return false;
        }

        return OperatingSystem.IsLinux()
            ? LinuxCgroupContainmentLease.IsCurrentProcessInCgroup(membershipId)
            : OperatingSystem.IsMacOS() &&
              MacProcessGroupContainmentLease.ContainsProcess(
                  processGroupId,
                  Environment.ProcessId);
    }

    public static void TerminateCurrentOwnership(
        string containmentId,
        string membershipId)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsJobContainmentLease.TerminateNamedJob(containmentId);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LinuxCgroupContainmentLease.TerminateCgroup(membershipId);
            return;
        }

        if (OperatingSystem.IsMacOS() &&
            int.TryParse(
                containmentId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processGroupId))
        {
            PosixProcessGroupTermination.Terminate(processGroupId);
            return;
        }

        throw new PlatformNotSupportedException(
            "The current platform does not provide an owned-process termination backend.");
    }

    public static void PrepareCurrentProcessForMembershipObservation(
        string ownerLifetimeId)
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxCgroupContainmentLease.MoveCurrentProcessToCgroup(ownerLifetimeId);
        }
    }
}

internal sealed partial class WindowsJobContainmentLease : IProcessContainmentLease
{
    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectTerminate = 0x0008;
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

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public bool MembershipRequiresAnchorExit => true;

    public static WindowsJobContainmentLease Prepare(string jobName)
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

            var lease = new WindowsJobContainmentLease(
                job,
                new ProcessOwnershipMetadata(
                    ProcessIdentityAuthority.WindowsProcessHandle,
                    ProcessContainmentKind.WindowsJobObject,
                    ProcessContainmentStrength.KernelJobTree,
                    jobName,
                    ProcessMembershipAuthority.WindowsJobObject,
                    jobName,
                    jobName,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    OwnershipEstablished: false,
                    ownerWasAlreadyContained));
            job = null;
            return lease;
        }
        finally
        {
            job?.Dispose();
        }
    }

    public void Establish(Process supervisor, ProcessOwnershipMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        var ownershipEstablished =
            !mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership) &&
            WindowsNative.AssignProcessToJobObject(_job, supervisor.Handle);
        if (mutation == ProcessOwnershipMutation.None && !ownershipEstablished)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The supervisor could not join its owned Job Object.");
        }

        Metadata = Metadata with { OwnershipEstablished = ownershipEstablished };
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

    public static void TerminateNamedJob(string jobName)
    {
        using var job = WindowsNative.OpenJobObject(
            JobObjectTerminate,
            inheritHandle: false,
            jobName);
        if (job.IsInvalid || !WindowsNative.TerminateJobObject(job, 1))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The owner-lifetime channel could not terminate its Windows Job Object.");
        }
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

    public void MarkAnchorReaped()
    {
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

internal sealed class TerminationFailureMutationContainmentLease : IProcessContainmentLease
{
    private readonly IProcessContainmentLease _inner;

    public TerminationFailureMutationContainmentLease(IProcessContainmentLease inner)
    {
        _inner = inner;
    }

    public ProcessOwnershipMetadata Metadata => _inner.Metadata;

    public bool MembershipRequiresAnchorExit => _inner.MembershipRequiresAnchorExit;

    public void Establish(Process supervisor, ProcessOwnershipMutation mutation)
    {
        _inner.Establish(supervisor, mutation);
    }

    public bool IsTreeQuiescent()
    {
        return _inner.IsTreeQuiescent();
    }

    public void Terminate()
    {
        _inner.Terminate();
        throw new InvalidOperationException("Injected containment termination failure.");
    }

    public void MarkAnchorReaped()
    {
        _inner.MarkAnchorReaped();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

internal sealed class MembershipFailureMutationContainmentLease : IProcessContainmentLease
{
    private readonly IProcessContainmentLease _inner;

    public MembershipFailureMutationContainmentLease(IProcessContainmentLease inner)
    {
        _inner = inner;
    }

    public ProcessOwnershipMetadata Metadata => _inner.Metadata;

    public bool MembershipRequiresAnchorExit => _inner.MembershipRequiresAnchorExit;

    public void Establish(Process supervisor, ProcessOwnershipMutation mutation)
    {
        _inner.Establish(supervisor, mutation);
    }

    public bool IsTreeQuiescent()
    {
        throw new InvalidOperationException("Injected authoritative membership-query failure.");
    }

    public void Terminate()
    {
        _inner.Terminate();
    }

    public void MarkAnchorReaped()
    {
        _inner.MarkAnchorReaped();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

internal static class PosixProcessGroupTermination
{
    private const int KillSignal = 9;
    private const int NoSuchProcess = 3;

    public static void Terminate(int processGroupId)
    {
        if (PosixNative.SignalProcessGroup(processGroupId, KillSignal) != 0 &&
            Marshal.GetLastPInvokeError() != NoSuchProcess)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
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

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int processId, int signal);

    public static int SignalProcessGroup(int processGroupId, int signal)
    {
        return Kill(-processGroupId, signal);
    }
}
