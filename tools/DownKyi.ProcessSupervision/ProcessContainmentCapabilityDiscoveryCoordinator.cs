using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

internal static class ProcessContainmentCapabilityDiscoveryCoordinator
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Provider and enumerable failures cross an untrusted discovery seam and must become typed fail-closed results.")]
    internal static ProcessContainmentCapabilityDiscoveryResult Discover(
        IEnumerable<ProcessContainmentBackendRegistration>? registrations)
    {
        if (registrations is null)
        {
            return Reject(
                ProcessContainmentCapabilityDiscoveryFailureKind.RegistrationEnumerationFailed,
                [],
                nameof(ArgumentNullException),
                "Containment backend registrations were not supplied.");
        }

        ProcessContainmentBackendRegistration[] snapshot;
        try
        {
            snapshot = registrations.ToArray();
        }
        catch (Exception failure)
        {
            return Reject(
                ProcessContainmentCapabilityDiscoveryFailureKind.RegistrationEnumerationFailed,
                [],
                failure.GetType().Name,
                "Containment backend registration enumeration failed.");
        }

        if (snapshot.Any(registration => registration is null))
        {
            return Reject(
                ProcessContainmentCapabilityDiscoveryFailureKind.InvalidRegistration,
                [],
                nameof(ArgumentException),
                "Containment backend registrations cannot contain a null item.");
        }

        var ordered = snapshot
            .OrderBy(
                registration => registration.BackendIdentity.Value,
                StringComparer.Ordinal)
            .ToArray();
        var duplicateIdentities = ordered
            .GroupBy(registration => registration.BackendIdentity)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIdentities.Length > 0)
        {
            return Reject(
                ProcessContainmentCapabilityDiscoveryFailureKind.DuplicateBackendIdentity,
                duplicateIdentities,
                nameof(InvalidOperationException),
                "Containment backend registrations must use unique identities.");
        }

        var discoveries = new List<ProcessContainmentBackendDiscovery>(
            ordered.Length);
        foreach (var registration in ordered)
        {
            ProcessContainmentCapabilityReport? capability;
            try
            {
                capability = registration.CapabilityProvider.DiscoverCapability();
            }
            catch (Exception failure)
            {
                return Reject(
                    ProcessContainmentCapabilityDiscoveryFailureKind.CapabilityProviderFailed,
                    [registration.BackendIdentity],
                    failure.GetType().Name,
                    "Containment capability provider failed.");
            }

            if (capability is null)
            {
                return Reject(
                    ProcessContainmentCapabilityDiscoveryFailureKind.InvalidCapabilityReport,
                    [registration.BackendIdentity],
                    nameof(InvalidOperationException),
                    "Containment capability provider returned no report.");
            }

            discoveries.Add(new ProcessContainmentBackendDiscovery(
                registration.BackendIdentity,
                registration.Platform,
                registration.ExecutionHandle,
                capability));
        }

        return new ProcessContainmentCapabilityDiscoveryCompleted(
            new ProcessContainmentDiscoveryBatch(discoveries.ToArray()));
    }

    private static ProcessContainmentCapabilityDiscoveryRejected Reject(
        ProcessContainmentCapabilityDiscoveryFailureKind kind,
        IEnumerable<ProcessContainmentBackendIdentity> backendIdentities,
        string errorType,
        string detail)
    {
        return new ProcessContainmentCapabilityDiscoveryRejected(
            new ProcessContainmentCapabilityDiscoveryFailure(
                kind,
                backendIdentities,
                errorType,
                detail));
    }
}
