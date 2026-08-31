using System.Collections.ObjectModel;

namespace DownKyi.ProcessSupervision;

internal enum ProcessContainmentPlatform
{
    Windows,
    Linux,
    MacOS
}

internal enum ProcessContainmentCapabilityState
{
    Unknown,
    Unavailable,
    Proven
}

internal sealed record ProcessContainmentBackendIdentity
{
    internal ProcessContainmentBackendIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Containment backend identity must not contain leading or trailing whitespace.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}

internal interface IProcessContainmentCapabilityProvider
{
    ProcessContainmentCapabilityDiscovery DiscoverCapability();
}

internal interface IProcessContainmentBackend : IProcessContainmentCapabilityProvider
{
    ProcessContainmentBackendIdentity Identity { get; }

    ProcessContainmentPlatform Platform { get; }
}

internal sealed record ProcessContainmentCapabilityEvidence
{
    internal ProcessContainmentCapabilityEvidence(
        ProcessContainmentCapabilityState state,
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        State = state;
        Detail = detail;
    }

    public ProcessContainmentCapabilityState State { get; }

    public string Detail { get; }
}

internal sealed record ProcessContainmentCapabilityDiscovery
{
    internal ProcessContainmentCapabilityDiscovery(
        ProcessContainmentBackendIdentity backendIdentity,
        ProcessContainmentPlatform platform,
        IEnumerable<ProcessContainmentCapabilityEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(backendIdentity);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }

        var snapshot = evidence.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException(
                "Capability evidence cannot contain a null item.",
                nameof(evidence));
        }

        BackendIdentity = backendIdentity;
        Platform = platform;
        Evidence = new ReadOnlyCollection<ProcessContainmentCapabilityEvidence>(
            snapshot);
    }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform Platform { get; }

    public IReadOnlyList<ProcessContainmentCapabilityEvidence> Evidence { get; }
}

internal sealed record ProcessContainmentBackendDiscovery
{
    internal ProcessContainmentBackendDiscovery(
        IProcessContainmentBackend backend,
        ProcessContainmentCapabilityDiscovery capability)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(capability);
        var backendIdentity = backend.Identity;
        ArgumentNullException.ThrowIfNull(backendIdentity);
        var backendPlatform = backend.Platform;
        if (!Enum.IsDefined(backendPlatform))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backend),
                backendPlatform,
                "Containment backend platform is invalid.");
        }

        Backend = backend;
        BackendIdentity = backendIdentity;
        BackendPlatform = backendPlatform;
        Capability = capability;
    }

    public IProcessContainmentBackend Backend { get; }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform BackendPlatform { get; }

    public ProcessContainmentCapabilityDiscovery Capability { get; }
}

internal abstract record ProcessContainmentBackendSelectionResult;

internal sealed record ProcessContainmentBackendSelected
    : ProcessContainmentBackendSelectionResult
{
    internal ProcessContainmentBackendSelected(
        IProcessContainmentBackend backend,
        ProcessContainmentCapabilityDiscovery capability)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(capability);
        Backend = backend;
        Capability = capability;
    }

    public IProcessContainmentBackend Backend { get; }

    public ProcessContainmentCapabilityDiscovery Capability { get; }
}

internal enum ProcessContainmentSelectionFailureKind
{
    DuplicateBackendIdentity,
    BackendIdentityMismatch,
    BackendPlatformMismatch,
    ContradictoryCapabilityEvidence,
    UnknownCapability,
    NoProvenBackend,
    MultipleProvenBackends
}

internal sealed record ProcessContainmentSelectionFailure
{
    internal ProcessContainmentSelectionFailure(
        ProcessContainmentSelectionFailureKind kind,
        IEnumerable<ProcessContainmentBackendIdentity> backendIdentities,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(backendIdentities);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Kind = kind;
        BackendIdentities = new ReadOnlyCollection<ProcessContainmentBackendIdentity>(
            backendIdentities
                .Distinct()
                .OrderBy(identity => identity.Value, StringComparer.Ordinal)
                .ToArray());
        Detail = detail;
    }

    public ProcessContainmentSelectionFailureKind Kind { get; }

    public IReadOnlyList<ProcessContainmentBackendIdentity> BackendIdentities { get; }

    public string Detail { get; }
}

internal sealed record ProcessContainmentBackendRejected
    : ProcessContainmentBackendSelectionResult
{
    internal ProcessContainmentBackendRejected(
        ProcessContainmentSelectionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public ProcessContainmentSelectionFailure Failure { get; }
}

internal sealed record EstablishedProcessContainmentFact
{
    internal EstablishedProcessContainmentFact(
        ProcessContainmentBackendIdentity backendIdentity,
        ProcessContainmentPlatform platform,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(backendIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }

        BackendIdentity = backendIdentity;
        Platform = platform;
        Detail = detail;
    }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform Platform { get; }

    public string Detail { get; }
}
