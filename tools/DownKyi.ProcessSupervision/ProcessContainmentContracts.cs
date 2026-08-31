using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

internal enum ProcessContainmentPlatform
{
    Unsupported,
    Windows,
    Linux,
    MacOS
}

internal enum ProcessContainmentBackendKind
{
    WindowsJob,
    LinuxDelegatedCgroup,
    LinuxProcessGroup,
    MacProcessGroup
}

internal enum LinuxCgroupAvailability
{
    WritableDelegation,
    DefinitelyUnavailable,
    Ambiguous
}

internal enum ContainmentOccupancy
{
    Occupied,
    Quiescent
}

internal enum QuiescenceObservationPoint
{
    BeforeAnchorReap,
    AfterAnchorReap
}

internal enum ContainmentAuthorityFailureKind
{
    UnsupportedPlatform,
    AmbiguousCapability,
    AuthorityUnavailable,
    MembershipAmbiguous,
    InvalidAnchorState,
    OperationFailed
}

internal readonly record struct LinuxCgroupCapability(
    LinuxCgroupAvailability Availability,
    string? ParentMembershipId,
    string? ParentDirectory,
    string? Detail)
{
    public static LinuxCgroupCapability DefinitelyUnavailable(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new LinuxCgroupCapability(
            LinuxCgroupAvailability.DefinitelyUnavailable,
            null,
            null,
            detail);
    }

    public static LinuxCgroupCapability Ambiguous(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new LinuxCgroupCapability(
            LinuxCgroupAvailability.Ambiguous,
            null,
            null,
            detail);
    }
}

internal readonly record struct PlatformContainmentFacts(
    ProcessContainmentPlatform Platform,
    LinuxCgroupCapability LinuxCgroup);

internal sealed record ContainmentAttachment(
    ProcessContainmentBackendKind BackendKind,
    string ContainmentId,
    string MembershipId,
    string OwnerLifetimeId);

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Containment authority failures always require a typed reason and message.")]
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "This exception is an internal contract between process-supervision owners.")]
internal sealed class ContainmentAuthorityException : Exception
{
    public ContainmentAuthorityException(
        ContainmentAuthorityFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ContainmentAuthorityFailureKind Kind { get; }
}

internal interface IProcessContainmentBackend
{
    ProcessContainmentBackendKind Kind { get; }

    IProcessContainmentLease Prepare(
        Process anchor,
        PlatformContainmentFacts facts);

    void EstablishCurrentProcess(ContainmentAttachment attachment);

    void PrepareCurrentProcessForObservation(ContainmentAttachment attachment);

    void TerminateCurrentProcessTree(ContainmentAttachment attachment);
}

internal interface IProcessContainmentLease : IDisposable
{
    ProcessOwnershipMetadata Metadata { get; }

    ContainmentAttachment Attachment { get; }

    QuiescenceObservationPoint ObservationPoint { get; }

    void AttachAnchor(Process anchor);

    void AssertAnchorOwned(Process anchor);

    ContainmentOccupancy ObserveQuiescence();

    void Terminate();

    void MarkAnchorReaped();
}
