using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal sealed class MacProcessGroupContainmentBackend : IProcessContainmentBackend
{
    public ProcessContainmentBackendKind Kind =>
        ProcessContainmentBackendKind.MacProcessGroup;

    public IProcessContainmentLease Prepare(
        Process anchor,
        PlatformContainmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (facts.Platform != ProcessContainmentPlatform.MacOS)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.UnsupportedPlatform,
                "The macOS process-group backend requires macOS facts.");
        }

        return new MacProcessGroupContainmentLease(anchor.Id);
    }

    public void EstablishCurrentProcess(ContainmentAttachment attachment)
    {
        var processGroupId = ParseAttachment(attachment);
        PosixProcessGroupNative.EstablishCurrentProcessGroup(processGroupId);
        MacProcessGroupContainmentLease.AssertMembership(
            processGroupId,
            Environment.ProcessId);
    }

    public void PrepareCurrentProcessForObservation(ContainmentAttachment attachment)
    {
        _ = ParseAttachment(attachment);
    }

    public void TerminateCurrentProcessTree(ContainmentAttachment attachment)
    {
        PosixProcessGroupNative.Terminate(
            ParseAttachment(attachment),
            allowDarwinPermissionDenied: true);
    }

    private static int ParseAttachment(ContainmentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.BackendKind != ProcessContainmentBackendKind.MacProcessGroup ||
            !int.TryParse(
                attachment.ContainmentId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processGroupId) ||
            processGroupId <= 0)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS process-group attachment is invalid.");
        }

        return processGroupId;
    }
}

internal sealed partial class MacProcessGroupContainmentLease : IProcessContainmentLease
{
    private const int MinimumSnapshotCapacity = 32;
    private const int SnapshotHeadroom = 32;
    private const int MaximumSnapshotCapacity = 1024 * 1024;
    private const int NoSuchProcess = 3;

    private readonly int _processGroupId;
    private bool _anchorReaped;
    private bool _terminationRequested;

    public MacProcessGroupContainmentLease(int anchorProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorProcessId);

        _processGroupId = anchorProcessId;
        var identity = anchorProcessId.ToString(CultureInfo.InvariantCulture);
        Attachment = new ContainmentAttachment(
            ProcessContainmentBackendKind.MacProcessGroup,
            identity,
            identity,
            identity);
        Metadata = new ProcessOwnershipMetadata(
            ProcessIdentityAuthority.DirectChildWait,
            ProcessContainmentKind.MacOSProcessGroup,
            ProcessContainmentStrength.TrustedChildProcessGroup,
            ProcessMembershipAuthority.MacOSLibprocProcessGroup,
            identity,
            identity,
            identity,
            OwnershipEstablished: false);
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public ContainmentAttachment Attachment { get; }

    public QuiescenceObservationPoint ObservationPoint =>
        QuiescenceObservationPoint.BeforeAnchorReap;

    public void AttachAnchor(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.Id != _processGroupId)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS process-group anchor identity changed before attachment.");
        }
    }

    public void AssertAnchorOwned(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        PosixProcessGroupNative.AssertProcessGroupMembership(anchor.Id, _processGroupId);
        AssertMembership(_processGroupId, anchor.Id);
        Metadata = Metadata with { OwnershipEstablished = true };
    }

    public ContainmentOccupancy ObserveQuiescence()
    {
        EnsureAnchorRetained();
        return ClassifyMembershipSnapshot(
            _processGroupId,
            QueryMembers(_processGroupId, _terminationRequested),
            _terminationRequested);
    }

    public void Terminate()
    {
        EnsureAnchorRetained();
        PosixProcessGroupNative.Terminate(
            _processGroupId,
            allowDarwinPermissionDenied: true);
        _terminationRequested = true;
    }

    public void MarkAnchorReaped()
    {
        _anchorReaped = true;
    }

    public void Dispose()
    {
    }

    public static void AssertMembership(int processGroupId, int processId)
    {
        if (!QueryMembers(processGroupId, allowTerminatedAbsence: false).Contains(processId))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "libproc did not confirm process-group membership.");
        }
    }

    internal static IReadOnlySet<int> QueryMembersForTesting(int processGroupId)
    {
        return QueryMembers(processGroupId, allowTerminatedAbsence: false);
    }

    internal static ContainmentOccupancy ClassifyMembershipSnapshotForTesting(
        int anchorProcessId,
        IEnumerable<int> processIds,
        bool terminationRequested = false)
    {
        return ClassifyMembershipSnapshot(
            anchorProcessId,
            processIds,
            terminationRequested);
    }

    internal static int CalculateSnapshotCapacityForTesting(int suggestedCount)
    {
        return CalculateSnapshotCapacity(suggestedCount);
    }

    internal static void ValidateSnapshotCountForTesting(int count, int capacity)
    {
        ValidateSnapshotCount(count, capacity);
    }

    private void EnsureAnchorRetained()
    {
        if (_anchorReaped)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.InvalidAnchorState,
                "macOS process-group authority requires the anchor to remain unreaped.");
        }
    }

    private static unsafe HashSet<int> QueryMembers(
        int processGroupId,
        bool allowTerminatedAbsence)
    {
        if (processGroupId <= 0)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS process-group identity is invalid.");
        }

        Marshal.SetLastPInvokeError(0);
        var capacity = MacOSNative.ListProcessGroupPids(processGroupId, null, 0);
        var initialError = Marshal.GetLastPInvokeError();
        if (capacity == 0 && initialError != 0)
        {
            if (allowTerminatedAbsence && initialError == NoSuchProcess)
            {
                return [];
            }

            throw OperationFailure(initialError, "The macOS membership authority is unavailable.");
        }

        capacity = CalculateSnapshotCapacity(capacity);
        var processIds = new int[capacity];
        int count;
        fixed (int* buffer = processIds)
        {
            Marshal.SetLastPInvokeError(0);
            count = MacOSNative.ListProcessGroupPids(
                processGroupId,
                buffer,
                checked(capacity * sizeof(int)));
        }

        var queryError = Marshal.GetLastPInvokeError();
        if (count == 0 && queryError != 0)
        {
            if (allowTerminatedAbsence && queryError == NoSuchProcess)
            {
                return [];
            }

            throw OperationFailure(queryError, "The macOS membership query failed.");
        }

        ValidateSnapshotCount(count, capacity);

        var members = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            if (processIds[index] <= 0 || !members.Add(processIds[index]))
            {
                throw new ContainmentAuthorityException(
                    ContainmentAuthorityFailureKind.MembershipAmbiguous,
                    "The macOS membership query returned ambiguous process identities.");
            }
        }

        return members;
    }

    private static ContainmentOccupancy ClassifyMembershipSnapshot(
        int anchorProcessId,
        IEnumerable<int> processIds,
        bool terminationRequested)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        var snapshot = processIds.ToArray();
        if (anchorProcessId <= 0 ||
            snapshot.Any(processId => processId <= 0) ||
            snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS membership snapshot contained invalid process identities.");
        }

        if (terminationRequested)
        {
            return snapshot.Length == 0
                ? ContainmentOccupancy.Quiescent
                : ContainmentOccupancy.Occupied;
        }

        if (snapshot.Length == 0 || !snapshot.Contains(anchorProcessId))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS success snapshot did not contain its retained anchor identity.");
        }

        return snapshot.Length == 1
            ? ContainmentOccupancy.Quiescent
            : ContainmentOccupancy.Occupied;
    }

    private static int CalculateSnapshotCapacity(int suggestedCount)
    {
        if (suggestedCount < 0 ||
            suggestedCount > MaximumSnapshotCapacity - SnapshotHeadroom)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS membership capacity suggestion was invalid.");
        }

        return checked(Math.Max(suggestedCount, MinimumSnapshotCapacity) + SnapshotHeadroom);
    }

    private static void ValidateSnapshotCount(int count, int capacity)
    {
        if (capacity <= 0 || count < 0 || count >= capacity)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The macOS membership snapshot was invalid or filled its bounded buffer.");
        }
    }

    private static ContainmentAuthorityException OperationFailure(int error, string message)
    {
        return new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.OperationFailed,
            message,
            new Win32Exception(error));
    }

    private static unsafe partial class MacOSNative
    {
        [LibraryImport(
            "/usr/lib/libproc.dylib",
            EntryPoint = "proc_listpgrppids",
            SetLastError = true)]
        public static partial int ListProcessGroupPids(
            int processGroupId,
            int* processIds,
            int bufferSize);
    }
}
