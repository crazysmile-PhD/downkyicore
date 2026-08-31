using System.Diagnostics;
using System.Globalization;

namespace DownKyi.ProcessSupervision;

internal sealed class LinuxCgroupContainmentBackend : IProcessContainmentBackend
{
    public ProcessContainmentBackendKind Kind =>
        ProcessContainmentBackendKind.LinuxDelegatedCgroup;

    public IProcessContainmentLease Prepare(
        Process anchor,
        PlatformContainmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (facts.Platform != ProcessContainmentPlatform.Linux ||
            facts.LinuxCgroup.Availability != LinuxCgroupAvailability.WritableDelegation)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AuthorityUnavailable,
                "The delegated cgroup backend requires a probed writable Linux delegation.");
        }

        return LinuxCgroupContainmentLease.Prepare(anchor, facts.LinuxCgroup);
    }

    public void EstablishCurrentProcess(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
        LinuxCgroupContainmentLease.AssertCurrentProcessMembership(attachment.MembershipId);
    }

    public void PrepareCurrentProcessForObservation(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
        LinuxCgroupContainmentLease.MoveCurrentProcess(attachment.OwnerLifetimeId);
    }

    public void TerminateCurrentProcessTree(ContainmentAttachment attachment)
    {
        ValidateAttachment(attachment);
        LinuxCgroupContainmentLease.TerminateMembership(attachment.MembershipId);
    }

    private static void ValidateAttachment(ContainmentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.BackendKind != ProcessContainmentBackendKind.LinuxDelegatedCgroup)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The delegated cgroup attachment is invalid.");
        }
    }
}

internal sealed class LinuxCgroupContainmentLease : IProcessContainmentLease
{
    private readonly string _directoryPath;
    private bool _anchorReaped;

    private LinuxCgroupContainmentLease(
        string directoryPath,
        ProcessOwnershipMetadata metadata,
        ContainmentAttachment attachment)
    {
        _directoryPath = directoryPath;
        Metadata = metadata;
        Attachment = attachment;
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public ContainmentAttachment Attachment { get; }

    public QuiescenceObservationPoint ObservationPoint =>
        QuiescenceObservationPoint.BeforeAnchorReap;

    public static LinuxCgroupContainmentLease Prepare(
        Process anchor,
        LinuxCgroupCapability capability)
    {
        var parentMembershipId = capability.ParentMembershipId;
        var parentDirectory = capability.ParentDirectory;
        if (string.IsNullOrWhiteSpace(parentMembershipId) ||
            string.IsNullOrWhiteSpace(parentDirectory) ||
            !string.Equals(
                Path.GetFullPath(parentDirectory),
                LinuxCgroupCapabilityProbe.ResolveMembershipDirectory(parentMembershipId),
                StringComparison.Ordinal))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AmbiguousCapability,
                "The writable cgroup capability fact is incomplete or inconsistent.");
        }

        var leaseName = $"downkyi-lease-{Guid.NewGuid():N}";
        var directory = Path.Combine(parentDirectory, leaseName);
        try
        {
            Directory.CreateDirectory(directory);
            ValidateFiles(directory);
            var membershipId = parentMembershipId == "/"
                ? $"/{leaseName}"
                : $"{parentMembershipId.TrimEnd('/')}/{leaseName}";
            var containmentId = anchor.Id.ToString(CultureInfo.InvariantCulture);
            return new LinuxCgroupContainmentLease(
                directory,
                new ProcessOwnershipMetadata(
                    ProcessIdentityAuthority.DirectChildWait,
                    ProcessContainmentKind.LinuxCgroupV2,
                    ProcessContainmentStrength.DelegatedCgroupTree,
                    ProcessMembershipAuthority.LinuxCgroupV2,
                    containmentId,
                    membershipId,
                    parentMembershipId,
                    OwnershipEstablished: false),
                new ContainmentAttachment(
                    ProcessContainmentBackendKind.LinuxDelegatedCgroup,
                    containmentId,
                    membershipId,
                    parentMembershipId));
        }
        catch (Exception failure)
        {
            Exception? rollbackFailure = null;
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                rollbackFailure = cleanupFailure;
            }

            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.OperationFailed,
                rollbackFailure == null
                    ? "Delegated cgroup preparation failed."
                    : "Delegated cgroup preparation and rollback both failed.",
                rollbackFailure == null
                    ? failure
                    : new AggregateException(failure, rollbackFailure));
        }
    }

    public void AttachAnchor(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        WriteProcessId(_directoryPath, anchor.Id);
    }

    public void AssertAnchorOwned(Process anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var actual = LinuxCgroupCapabilityProbe.ParseUnifiedMembership(
            File.ReadAllLines($"/proc/{anchor.Id.ToString(CultureInfo.InvariantCulture)}/cgroup"));
        if (!string.Equals(actual, Attachment.MembershipId, StringComparison.Ordinal))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The inert anchor did not enter its delegated cgroup.");
        }

        Metadata = Metadata with { OwnershipEstablished = true };
    }

    public ContainmentOccupancy ObserveQuiescence()
    {
        if (_anchorReaped)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.InvalidAnchorState,
                "Delegated cgroup occupancy must be observed before anchor reap.");
        }

        var values = File.ReadAllLines(Path.Combine(_directoryPath, "cgroup.events"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 &&
                            string.Equals(parts[0], "populated", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .ToArray();
        if (values.Length != 1 || values[0] is not ("0" or "1"))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The delegated cgroup occupancy state is malformed or ambiguous.");
        }

        return values[0] == "0"
            ? ContainmentOccupancy.Quiescent
            : ContainmentOccupancy.Occupied;
    }

    public void Terminate()
    {
        File.WriteAllText(Path.Combine(_directoryPath, "cgroup.kill"), "1");
    }

    public void MarkAnchorReaped()
    {
        _anchorReaped = true;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath);
        }
    }

    public static void AssertCurrentProcessMembership(string membershipId)
    {
        var actual = LinuxCgroupCapabilityProbe.ParseUnifiedMembership(
            File.ReadAllLines("/proc/self/cgroup"));
        if (!string.Equals(actual, membershipId, StringComparison.Ordinal))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The inert anchor did not confirm delegated cgroup membership.");
        }
    }

    public static void MoveCurrentProcess(string membershipId)
    {
        var directory = LinuxCgroupCapabilityProbe.ResolveMembershipDirectory(membershipId);
        WriteProcessId(directory, Environment.ProcessId);
        AssertCurrentProcessMembership(membershipId);
    }

    public static void TerminateMembership(string membershipId)
    {
        var directory = LinuxCgroupCapabilityProbe.ResolveMembershipDirectory(membershipId);
        ValidateFiles(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup.kill"), "1");
    }

    private static void WriteProcessId(string directory, int processId)
    {
        File.WriteAllText(
            Path.Combine(directory, "cgroup.procs"),
            processId.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateFiles(string directory)
    {
        foreach (var fileName in new[] { "cgroup.events", "cgroup.procs", "cgroup.kill" })
        {
            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                throw new ContainmentAuthorityException(
                    ContainmentAuthorityFailureKind.AuthorityUnavailable,
                    $"The delegated cgroup does not expose required file '{fileName}'.");
            }
        }
    }
}
