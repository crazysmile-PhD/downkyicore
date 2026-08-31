using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.ProcessSupervision;

internal sealed class WindowsJobContainmentBackend : IProcessContainmentBackend
{
    public ProcessContainmentBackendKind Kind => ProcessContainmentBackendKind.WindowsJob;

    public IProcessContainmentLease Prepare(
        Process anchor,
        PlatformContainmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (facts.Platform != ProcessContainmentPlatform.Windows)
        {
            throw WrongPlatform();
        }

        return WindowsJobContainmentLease.Prepare(
            anchor,
            $"Local\\DownKyi.ProcessLease.{Guid.NewGuid():N}");
    }

    public void EstablishCurrentProcess(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
        WindowsJobContainmentLease.AssertCurrentProcessInJob(attachment.ContainmentId);
    }

    public void PrepareCurrentProcessForObservation(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
    }

    public void TerminateCurrentProcessTree(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
        WindowsJobContainmentLease.TerminateNamedJob(attachment.ContainmentId);
    }

    private static void ValidateAttachment(ContainmentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.BackendKind != ProcessContainmentBackendKind.WindowsJob ||
            string.IsNullOrWhiteSpace(attachment.ContainmentId))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The Windows Job attachment is invalid.");
        }
    }

    private static ContainmentAuthorityException WrongPlatform()
    {
        return new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.UnsupportedPlatform,
            "The Windows Job backend requires Windows facts.");
    }
}

internal sealed partial class WindowsJobContainmentLease : IProcessContainmentLease
{
    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectTerminate = 0x0008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectBasicProcessIdListClass = 3;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int ErrorMoreData = 234;

    private readonly WindowsNative.SafeJobHandle _job;
    private readonly Process _anchor;
    private Dictionary<nuint, InfrastructureMember>? _infrastructureBaseline;
    private bool _anchorReaped;
    private bool _terminationRequested;

    private WindowsJobContainmentLease(
        WindowsNative.SafeJobHandle job,
        Process anchor,
        ProcessOwnershipMetadata metadata,
        ContainmentAttachment attachment)
    {
        _job = job;
        _anchor = anchor;
        Metadata = metadata;
        Attachment = attachment;
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public ContainmentAttachment Attachment { get; }

    public QuiescenceObservationPoint ObservationPoint =>
        QuiescenceObservationPoint.BeforeAnchorReap;

    public static WindowsJobContainmentLease Prepare(Process anchor, string jobName)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects require Windows.");
        }

        WindowsNative.SafeJobHandle? job = WindowsNative.CreateJobObject(IntPtr.Zero, jobName);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw OperationFailure("The Windows Job Object could not be created.");
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
                throw OperationFailure("The Windows Job Object kill-on-close policy failed.");
            }

            var attachment = new ContainmentAttachment(
                ProcessContainmentBackendKind.WindowsJob,
                jobName,
                jobName,
                jobName);
            var lease = new WindowsJobContainmentLease(
                job,
                anchor,
                new ProcessOwnershipMetadata(
                    ProcessIdentityAuthority.WindowsProcessHandle,
                    ProcessContainmentKind.WindowsJobObject,
                    ProcessContainmentStrength.KernelJobTree,
                    ProcessMembershipAuthority.WindowsJobAccounting,
                    jobName,
                    jobName,
                    jobName,
                    OwnershipEstablished: false),
                attachment);
            job = null;
            return lease;
        }
        finally
        {
            job?.Dispose();
        }
    }

    public void AttachAnchor(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        EnsurePreparedAnchor(anchor);
        if (!WindowsNative.AssignProcessToJobObject(_job, anchor.Handle))
        {
            throw OperationFailure("The inert anchor could not enter its Windows Job Object.");
        }
    }

    public void AssertAnchorOwned(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        EnsurePreparedAnchor(anchor);
        AssertProcessInJob(anchor.Handle, _job.DangerousGetHandle());
        _infrastructureBaseline ??= CaptureInfrastructureBaseline();
        Metadata = Metadata with { OwnershipEstablished = true };
    }

    public ContainmentOccupancy ObserveQuiescence()
    {
        EnsureAnchorNotReaped();
        var activeMembers = QueryActiveProcessIds();
        if (_terminationRequested)
        {
            return activeMembers.Count == 0
                ? ContainmentOccupancy.Quiescent
                : ContainmentOccupancy.Occupied;
        }

        var baseline = _infrastructureBaseline ?? throw new InvalidOperationException(
            "Windows Job occupancy cannot be observed before its infrastructure baseline.");
        var provenInfrastructure = new HashSet<nuint>();
        foreach (var processId in activeMembers)
        {
            if (!baseline.TryGetValue(processId, out var member))
            {
                continue;
            }

            if (!IsRetainedMemberLive(member))
            {
                throw new ContainmentAuthorityException(
                    ContainmentAuthorityFailureKind.MembershipAmbiguous,
                    "An active Windows Job member reused an exited infrastructure process identifier.");
            }
            provenInfrastructure.Add(processId);
        }

        return ClassifyActiveMembers(
            ReadAnchorHasExited(),
            activeMembers,
            provenInfrastructure,
            checked((nuint)_anchor.Id));
    }

    public void Terminate()
    {
        if (!WindowsNative.TerminateJobObject(_job, 1))
        {
            throw OperationFailure("The Windows Job termination request failed.");
        }

        _terminationRequested = true;
    }

    public void MarkAnchorReaped()
    {
        _anchorReaped = true;
    }

    public void Dispose()
    {
        // The lifecycle owner supplied the Process and retains disposal ownership.
        if (_infrastructureBaseline != null)
        {
            foreach (var member in _infrastructureBaseline.Values)
            {
                if (member.OwnsProcess)
                {
                    member.Process.Dispose();
                }
            }
        }
        _job.Dispose();
    }

    public static void AssertCurrentProcessInJob(string jobName)
    {
        using var job = WindowsNative.OpenJobObject(
            JobObjectQuery,
            inheritHandle: false,
            jobName);
        if (job.IsInvalid)
        {
            throw OperationFailure("The Windows Job membership authority could not be opened.");
        }

        using var process = Process.GetCurrentProcess();
        AssertProcessInJob(process.Handle, job.DangerousGetHandle());
    }

    public static void TerminateNamedJob(string jobName)
    {
        using var job = WindowsNative.OpenJobObject(
            JobObjectTerminate,
            inheritHandle: false,
            jobName);
        if (job.IsInvalid || !WindowsNative.TerminateJobObject(job, 1))
        {
            throw OperationFailure("The owner-lifetime path could not terminate its Windows Job Object.");
        }
    }

    private static void AssertProcessInJob(IntPtr process, IntPtr job)
    {
        if (!WindowsNative.IsProcessInJob(process, job, out var inJob))
        {
            throw OperationFailure("Windows Job membership could not be queried.");
        }

        if (!inJob)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The inert anchor is not a member of its Windows Job Object.");
        }
    }

    private void EnsureAnchorNotReaped()
    {
        if (_anchorReaped)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.InvalidAnchorState,
                "Windows Job occupancy must be observed before anchor reap.");
        }
    }

    internal static ContainmentOccupancy ClassifyActiveMembersForTesting(
        bool anchorHasExited,
        IEnumerable<nuint> activeMembers,
        IEnumerable<nuint> provenInfrastructure,
        nuint anchorProcessId,
        bool terminationRequested = false)
    {
        ArgumentNullException.ThrowIfNull(activeMembers);
        ArgumentNullException.ThrowIfNull(provenInfrastructure);
        var active = activeMembers.ToHashSet();
        if (terminationRequested)
        {
            return active.Count == 0
                ? ContainmentOccupancy.Quiescent
                : ContainmentOccupancy.Occupied;
        }

        return ClassifyActiveMembers(
            anchorHasExited,
            active,
            provenInfrastructure.ToHashSet(),
            anchorProcessId);
    }

    internal static void ValidateSnapshotCountsForTesting(
        uint expectedActiveProcesses,
        uint assignedProcesses,
        uint listedProcesses)
    {
        ValidateSnapshotCounts(
            expectedActiveProcesses,
            assignedProcesses,
            listedProcesses);
    }

    internal static void ValidateStableInfrastructureSnapshotForTesting(
        IEnumerable<nuint> firstSnapshot,
        IEnumerable<nuint> secondSnapshot)
    {
        ArgumentNullException.ThrowIfNull(firstSnapshot);
        ArgumentNullException.ThrowIfNull(secondSnapshot);
        ValidateStableInfrastructureSnapshot(
            firstSnapshot.ToHashSet(),
            secondSnapshot.ToHashSet());
    }

    private static ContainmentOccupancy ClassifyActiveMembers(
        bool anchorHasExited,
        HashSet<nuint> activeMembers,
        HashSet<nuint> provenInfrastructure,
        nuint anchorProcessId)
    {
        if (!anchorHasExited && !activeMembers.Contains(anchorProcessId))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "Windows reported a live retained anchor outside the active Job membership snapshot.");
        }

        if (anchorHasExited)
        {
            return ContainmentOccupancy.Occupied;
        }

        return activeMembers.All(provenInfrastructure.Contains)
            ? ContainmentOccupancy.Quiescent
            : ContainmentOccupancy.Occupied;
    }

    private void EnsurePreparedAnchor(Process anchor)
    {
        if (!ReferenceEquals(anchor, _anchor))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "Windows Job operations require the prepared anchor handle authority.");
        }
    }

    private bool ReadAnchorHasExited()
    {
        try
        {
            return _anchor.HasExited;
        }
        catch (Exception failure) when (
            failure is InvalidOperationException or Win32Exception)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AuthorityUnavailable,
                "The prepared Windows anchor handle authority is unavailable.",
                failure);
        }
    }

    private Dictionary<nuint, InfrastructureMember> CaptureInfrastructureBaseline()
    {
        var firstSnapshot = QueryActiveProcessIds();
        var members = new Dictionary<nuint, InfrastructureMember>();
        try
        {
            foreach (var processId in firstSnapshot)
            {
                var isAnchor = processId == checked((nuint)_anchor.Id);
                Process process;
                try
                {
                    process = isAnchor
                        ? _anchor
                        : Process.GetProcessById(checked((int)processId));
                }
                catch (Exception failure) when (
                    failure is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    throw new ContainmentAuthorityException(
                        ContainmentAuthorityFailureKind.MembershipAmbiguous,
                        "Windows Job infrastructure changed while its retained handle baseline was opened.",
                        failure);
                }

                var member = new InfrastructureMember(process, OwnsProcess: !isAnchor);
                try
                {
                    if (!IsRetainedMemberLive(member))
                    {
                        throw new ContainmentAuthorityException(
                            ContainmentAuthorityFailureKind.MembershipAmbiguous,
                            "Windows Job infrastructure exited while its retained handle baseline was opened.");
                    }
                    members.Add(processId, member);
                }
                catch
                {
                    if (member.OwnsProcess)
                    {
                        member.Process.Dispose();
                    }
                    throw;
                }
            }

            var secondSnapshot = QueryActiveProcessIds();
            ValidateStableInfrastructureSnapshot(firstSnapshot, secondSnapshot);
            if (!members.ContainsKey(checked((nuint)_anchor.Id)))
            {
                throw new ContainmentAuthorityException(
                    ContainmentAuthorityFailureKind.MembershipAmbiguous,
                    "The retained Windows Job anchor was absent from its infrastructure baseline.");
            }
            foreach (var member in members.Values)
            {
                if (!IsRetainedMemberLive(member))
                {
                    throw new ContainmentAuthorityException(
                        ContainmentAuthorityFailureKind.MembershipAmbiguous,
                        "Windows Job infrastructure exited before its baseline became authoritative.");
                }
            }

            return members;
        }
        catch
        {
            foreach (var member in members.Values)
            {
                if (member.OwnsProcess)
                {
                    member.Process.Dispose();
                }
            }
            throw;
        }
    }

    private bool IsRetainedMemberLive(InfrastructureMember member)
    {
        try
        {
            if (member.Process.HasExited)
            {
                return false;
            }
            AssertProcessInJob(member.Process.Handle, _job.DangerousGetHandle());
            return true;
        }
        catch (ContainmentAuthorityException)
        {
            throw;
        }
        catch (Exception failure) when (
            failure is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AuthorityUnavailable,
                "A retained Windows Job infrastructure handle became unavailable.",
                failure);
        }
    }

    private HashSet<nuint> QueryActiveProcessIds()
    {
        if (!WindowsNative.QueryInformationJobObject(
                _job,
                JobObjectBasicAccountingInformationClass,
                out JobObjectBasicAccountingInformation accounting,
                Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                out _))
        {
            throw OperationFailure("Windows Job accounting is unavailable.");
        }

        var capacity = Math.Max(accounting.ActiveProcesses, 1u);
        var bufferLength = checked(8 + checked((int)capacity) * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            if (!WindowsNative.QueryInformationJobObjectProcessIds(
                    _job,
                    JobObjectBasicProcessIdListClass,
                    buffer,
                    bufferLength,
                    out _))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorMoreData)
                {
                    throw new ContainmentAuthorityException(
                        ContainmentAuthorityFailureKind.MembershipAmbiguous,
                        "Windows Job membership changed while its process snapshot was captured.");
                }
                throw OperationFailure("Windows Job process membership is unavailable.", error);
            }

            var assigned = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var listed = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            ValidateSnapshotCounts(accounting.ActiveProcesses, assigned, listed);

            var processIds = new HashSet<nuint>();
            for (var index = 0; index < listed; index++)
            {
                var rawProcessId = Marshal.ReadIntPtr(
                    buffer,
                    checked(8 + index * IntPtr.Size));
                var processId = checked((nuint)rawProcessId.ToInt64());
                if (processId == 0 || !processIds.Add(processId))
                {
                    throw new ContainmentAuthorityException(
                        ContainmentAuthorityFailureKind.MembershipAmbiguous,
                        "Windows Job returned an invalid or duplicate process identifier.");
                }
            }
            return processIds;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateSnapshotCounts(
        uint expectedActiveProcesses,
        uint assignedProcesses,
        uint listedProcesses)
    {
        if (assignedProcesses != expectedActiveProcesses ||
            listedProcesses != assignedProcesses)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "Windows Job membership changed while its process snapshot was captured.");
        }
    }

    private static void ValidateStableInfrastructureSnapshot(
        HashSet<nuint> firstSnapshot,
        HashSet<nuint> secondSnapshot)
    {
        if (!firstSnapshot.SetEquals(secondSnapshot))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "Windows Job infrastructure membership changed while its baseline was captured.");
        }
    }

    private static ContainmentAuthorityException OperationFailure(string message)
    {
        return OperationFailure(message, Marshal.GetLastPInvokeError());
    }

    private static ContainmentAuthorityException OperationFailure(string message, int error)
    {
        return new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.OperationFailed,
            message,
            new Win32Exception(error));
    }

    private sealed record InfrastructureMember(Process Process, bool OwnsProcess);

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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private static partial class WindowsNative
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial SafeJobHandle CreateJobObject(
            IntPtr jobAttributes,
            string? name);

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

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "QueryInformationJobObject",
            SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool QueryInformationJobObjectProcessIds(
            SafeJobHandle job,
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }
    }
}
