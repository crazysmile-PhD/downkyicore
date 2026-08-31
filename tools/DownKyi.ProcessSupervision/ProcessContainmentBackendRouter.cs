namespace DownKyi.ProcessSupervision;

internal static class ProcessContainmentBackendRouter
{
    internal static ProcessContainmentBackendSelectionResult Select(
        ProcessContainmentPlatform platform,
        IEnumerable<ProcessContainmentBackendDiscovery> discoveries)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        var snapshot = discoveries
            .OrderBy(discovery => discovery.BackendIdentity.Value, StringComparer.Ordinal)
            .ToArray();

        var duplicateIdentities = snapshot
            .GroupBy(
                discovery => discovery.BackendIdentity,
                EqualityComparer<ProcessContainmentBackendIdentity>.Default)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIdentities.Length > 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.DuplicateBackendIdentity,
                duplicateIdentities,
                "Containment backend identities must be unique.");
        }

        var identityMismatches = snapshot
            .Where(discovery => discovery.BackendIdentity !=
                discovery.Capability.BackendIdentity)
            .ToArray();
        if (identityMismatches.Length > 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.BackendIdentityMismatch,
                identityMismatches.SelectMany(discovery =>
                    new[]
                    {
                        discovery.BackendIdentity,
                        discovery.Capability.BackendIdentity
                    }),
                "Capability evidence must name the backend that produced it.");
        }

        var platformMismatches = snapshot
            .Where(discovery => discovery.BackendPlatform !=
                discovery.Capability.Platform)
            .ToArray();
        if (platformMismatches.Length > 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.BackendPlatformMismatch,
                platformMismatches.Select(discovery => discovery.BackendIdentity),
                "Capability evidence platform must match its backend platform.");
        }

        var eligible = snapshot
            .Where(discovery => discovery.BackendPlatform == platform)
            .ToArray();
        var contradictory = eligible
            .Where(discovery => discovery.Capability.Evidence
                .Select(evidence => evidence.State)
                .Distinct()
                .Skip(1)
                .Any())
            .ToArray();
        if (contradictory.Length > 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.ContradictoryCapabilityEvidence,
                contradictory.Select(discovery => discovery.BackendIdentity),
                "A backend cannot publish more than one capability state.");
        }

        var unknown = eligible
            .Where(discovery => discovery.Capability.Evidence.Count == 0 ||
                discovery.Capability.Evidence.Any(evidence =>
                    evidence.State == ProcessContainmentCapabilityState.Unknown))
            .ToArray();
        if (unknown.Length > 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.UnknownCapability,
                unknown.Select(discovery => discovery.BackendIdentity),
                "Unknown containment capability forbids backend selection.");
        }

        var proven = eligible
            .Where(discovery => discovery.Capability.Evidence.Count > 0 &&
                discovery.Capability.Evidence.All(evidence =>
                    evidence.State == ProcessContainmentCapabilityState.Proven))
            .ToArray();
        if (proven.Length == 0)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.NoProvenBackend,
                eligible.Select(discovery => discovery.BackendIdentity),
                "No containment backend has proven capability for the selected platform.");
        }

        if (proven.Length > 1)
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.MultipleProvenBackends,
                proven.Select(discovery => discovery.BackendIdentity),
                "More than one containment backend proved capability for the selected platform.");
        }

        return new ProcessContainmentBackendSelected(
            proven[0].Backend,
            proven[0].Capability);
    }

    private static ProcessContainmentBackendRejected Reject(
        ProcessContainmentSelectionFailureKind kind,
        IEnumerable<ProcessContainmentBackendIdentity> backendIdentities,
        string detail)
    {
        return new ProcessContainmentBackendRejected(
            new ProcessContainmentSelectionFailure(
                kind,
                backendIdentities,
                detail));
    }
}
