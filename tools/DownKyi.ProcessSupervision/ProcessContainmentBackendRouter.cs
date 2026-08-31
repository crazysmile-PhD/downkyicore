namespace DownKyi.ProcessSupervision;

internal static class ProcessContainmentBackendRouter
{
    internal static ProcessContainmentBackendSelectionResult Select(
        ProcessContainmentPlatform platform,
        ProcessContainmentDiscoveryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!Enum.IsDefined(platform))
        {
            return Reject(
                ProcessContainmentSelectionFailureKind.InvalidRequestedPlatform,
                [],
                "Requested containment platform is invalid.");
        }

        var snapshot = batch.Discoveries
            .OrderBy(
                discovery => discovery.BackendIdentity.Value,
                StringComparer.Ordinal)
            .ToArray();
        var duplicateIdentities = snapshot
            .GroupBy(discovery => discovery.BackendIdentity)
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

        var eligible = snapshot
            .Where(discovery => discovery.Platform == platform)
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
            proven[0].BackendIdentity,
            proven[0].Platform,
            proven[0].ExecutionHandle,
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
