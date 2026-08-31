using System.Diagnostics;
using System.Globalization;

namespace DownKyi.ProcessSupervision;

internal sealed class LinuxProcessGroupContainmentBackend : IProcessContainmentBackend
{
    public ProcessContainmentBackendKind Kind =>
        ProcessContainmentBackendKind.LinuxProcessGroup;

    public IProcessContainmentLease Prepare(
        Process anchor,
        PlatformContainmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (facts.Platform != ProcessContainmentPlatform.Linux ||
            facts.LinuxCgroup.Availability != LinuxCgroupAvailability.DefinitelyUnavailable)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AuthorityUnavailable,
                "Linux process-group containment requires a definite absence of writable cgroup delegation.");
        }

        return new LinuxProcessGroupContainmentLease(anchor.Id);
    }

    public void EstablishCurrentProcess(ContainmentAttachment attachment)
    {
        var processGroupId = ParseAttachment(attachment);
        PosixProcessGroupNative.EstablishCurrentProcessGroup(processGroupId);
    }

    public void PrepareCurrentProcessForObservation(ContainmentAttachment attachment)
    {
        _ = ParseAttachment(attachment);
    }

    public void TerminateCurrentProcessTree(ContainmentAttachment attachment)
    {
        PosixProcessGroupNative.Terminate(
            ParseAttachment(attachment),
            allowDarwinPermissionDenied: false);
    }

    private static int ParseAttachment(ContainmentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.BackendKind != ProcessContainmentBackendKind.LinuxProcessGroup ||
            !int.TryParse(
                attachment.ContainmentId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processGroupId) ||
            processGroupId <= 0)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The Linux process-group attachment is invalid.");
        }

        return processGroupId;
    }
}

internal sealed class LinuxProcessGroupContainmentLease : IProcessContainmentLease
{
    private readonly int _processGroupId;
    private bool _anchorReaped;

    public LinuxProcessGroupContainmentLease(int anchorProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorProcessId);

        _processGroupId = anchorProcessId;
        var identity = anchorProcessId.ToString(CultureInfo.InvariantCulture);
        Attachment = new ContainmentAttachment(
            ProcessContainmentBackendKind.LinuxProcessGroup,
            identity,
            identity,
            identity);
        Metadata = new ProcessOwnershipMetadata(
            ProcessIdentityAuthority.DirectChildWait,
            ProcessContainmentKind.LinuxProcessGroup,
            ProcessContainmentStrength.TrustedChildProcessGroup,
            ProcessMembershipAuthority.LinuxProcessGroupSignal,
            identity,
            identity,
            identity,
            OwnershipEstablished: false);
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public ContainmentAttachment Attachment { get; }

    public QuiescenceObservationPoint ObservationPoint =>
        QuiescenceObservationPoint.AfterAnchorReap;

    public void AttachAnchor(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.Id != _processGroupId)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The Linux process-group anchor identity changed before attachment.");
        }
    }

    public void AssertAnchorOwned(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        PosixProcessGroupNative.AssertProcessGroupMembership(anchor.Id, _processGroupId);
        Metadata = Metadata with { OwnershipEstablished = true };
    }

    public ContainmentOccupancy ObserveQuiescence()
    {
        if (!_anchorReaped)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.InvalidAnchorState,
                "Linux process-group occupancy requires the anchor to be reaped first.");
        }

        return PosixProcessGroupNative.ObserveAfterAnchorReap(_processGroupId);
    }

    public void Terminate()
    {
        PosixProcessGroupNative.Terminate(
            _processGroupId,
            allowDarwinPermissionDenied: false);
    }

    public void MarkAnchorReaped()
    {
        _anchorReaped = true;
    }

    public void Dispose()
    {
    }
}
