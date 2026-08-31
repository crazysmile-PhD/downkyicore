using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

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
        if (!IsCanonicalToken(value))
        {
            throw new ArgumentException(
                "Containment backend identity must be a lowercase ASCII token separated by single hyphens.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    private static bool IsCanonicalToken(string value)
    {
        if (value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in value)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9'))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }
}

[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "The backend is deliberately an opaque execution handle; routing authority lives only in immutable descriptors.")]
internal interface IProcessContainmentBackend
{
}

internal interface IProcessContainmentCapabilityProvider
{
    ProcessContainmentCapabilityReport DiscoverCapability();
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

internal sealed record ProcessContainmentCapabilityReport
{
    internal ProcessContainmentCapabilityReport(
        IEnumerable<ProcessContainmentCapabilityEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var snapshot = evidence.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException(
                "Capability evidence cannot contain a null item.",
                nameof(evidence));
        }

        Evidence = new ReadOnlyCollection<ProcessContainmentCapabilityEvidence>(
            snapshot);
    }

    public IReadOnlyList<ProcessContainmentCapabilityEvidence> Evidence { get; }
}

internal sealed record ProcessContainmentBackendRegistration
{
    internal ProcessContainmentBackendRegistration(
        ProcessContainmentBackendIdentity backendIdentity,
        ProcessContainmentPlatform platform,
        IProcessContainmentBackend executionHandle,
        IProcessContainmentCapabilityProvider capabilityProvider)
    {
        ArgumentNullException.ThrowIfNull(backendIdentity);
        ArgumentNullException.ThrowIfNull(executionHandle);
        ArgumentNullException.ThrowIfNull(capabilityProvider);
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }

        BackendIdentity = backendIdentity;
        Platform = platform;
        ExecutionHandle = executionHandle;
        CapabilityProvider = capabilityProvider;
    }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform Platform { get; }

    public IProcessContainmentBackend ExecutionHandle { get; }

    public IProcessContainmentCapabilityProvider CapabilityProvider { get; }
}

internal sealed record ProcessContainmentBackendDiscovery
{
    internal ProcessContainmentBackendDiscovery(
        ProcessContainmentBackendIdentity backendIdentity,
        ProcessContainmentPlatform platform,
        IProcessContainmentBackend executionHandle,
        ProcessContainmentCapabilityReport capability)
    {
        ArgumentNullException.ThrowIfNull(backendIdentity);
        ArgumentNullException.ThrowIfNull(executionHandle);
        ArgumentNullException.ThrowIfNull(capability);
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }

        BackendIdentity = backendIdentity;
        Platform = platform;
        ExecutionHandle = executionHandle;
        Capability = capability;
    }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform Platform { get; }

    public IProcessContainmentBackend ExecutionHandle { get; }

    public ProcessContainmentCapabilityReport Capability { get; }
}

internal sealed class ProcessContainmentDiscoveryBatch
{
    internal ProcessContainmentDiscoveryBatch(
        ProcessContainmentBackendDiscovery[] discoveries)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        if (discoveries.Any(item => item is null))
        {
            throw new ArgumentException(
                "Containment discovery batch cannot contain a null item.",
                nameof(discoveries));
        }

        Discoveries = new ReadOnlyCollection<ProcessContainmentBackendDiscovery>(
            discoveries.ToArray());
    }

    public IReadOnlyList<ProcessContainmentBackendDiscovery> Discoveries { get; }
}

internal abstract record ProcessContainmentCapabilityDiscoveryResult;

internal sealed record ProcessContainmentCapabilityDiscoveryCompleted
    : ProcessContainmentCapabilityDiscoveryResult
{
    internal ProcessContainmentCapabilityDiscoveryCompleted(
        ProcessContainmentDiscoveryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Batch = batch;
    }

    public ProcessContainmentDiscoveryBatch Batch { get; }
}

internal enum ProcessContainmentCapabilityDiscoveryFailureKind
{
    RegistrationEnumerationFailed,
    InvalidRegistration,
    DuplicateBackendIdentity,
    CapabilityProviderFailed,
    InvalidCapabilityReport
}

internal sealed record ProcessContainmentCapabilityDiscoveryFailure
{
    internal ProcessContainmentCapabilityDiscoveryFailure(
        ProcessContainmentCapabilityDiscoveryFailureKind kind,
        IEnumerable<ProcessContainmentBackendIdentity> backendIdentities,
        string errorType,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(backendIdentities);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Kind = kind;
        BackendIdentities = new ReadOnlyCollection<ProcessContainmentBackendIdentity>(
            backendIdentities
                .Distinct()
                .OrderBy(identity => identity.Value, StringComparer.Ordinal)
                .ToArray());
        ErrorType = errorType;
        Detail = detail;
    }

    public ProcessContainmentCapabilityDiscoveryFailureKind Kind { get; }

    public IReadOnlyList<ProcessContainmentBackendIdentity> BackendIdentities { get; }

    public string ErrorType { get; }

    public string Detail { get; }
}

internal sealed record ProcessContainmentCapabilityDiscoveryRejected
    : ProcessContainmentCapabilityDiscoveryResult
{
    internal ProcessContainmentCapabilityDiscoveryRejected(
        ProcessContainmentCapabilityDiscoveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public ProcessContainmentCapabilityDiscoveryFailure Failure { get; }
}

internal abstract record ProcessContainmentBackendSelectionResult;

internal sealed record ProcessContainmentBackendSelected
    : ProcessContainmentBackendSelectionResult
{
    internal ProcessContainmentBackendSelected(
        ProcessContainmentBackendIdentity backendIdentity,
        ProcessContainmentPlatform platform,
        IProcessContainmentBackend executionHandle,
        ProcessContainmentCapabilityReport capability)
    {
        ArgumentNullException.ThrowIfNull(backendIdentity);
        ArgumentNullException.ThrowIfNull(executionHandle);
        ArgumentNullException.ThrowIfNull(capability);
        BackendIdentity = backendIdentity;
        Platform = platform;
        ExecutionHandle = executionHandle;
        Capability = capability;
    }

    public ProcessContainmentBackendIdentity BackendIdentity { get; }

    public ProcessContainmentPlatform Platform { get; }

    public IProcessContainmentBackend ExecutionHandle { get; }

    public ProcessContainmentCapabilityReport Capability { get; }
}

internal enum ProcessContainmentSelectionFailureKind
{
    InvalidRequestedPlatform,
    DuplicateBackendIdentity,
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
