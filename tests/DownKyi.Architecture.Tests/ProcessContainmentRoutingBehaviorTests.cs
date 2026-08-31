using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessContainmentRoutingBehaviorTests
{
    [Fact]
    public void ExactlyOneProvenBackendIsSelectedIndependentOfProviderOrder()
    {
        var proven = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);
        var unavailableA = Backend(
            "windows-alt-a",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);
        var unavailableB = Backend(
            "windows-alt-b",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);
        var providers = new[] { proven, unavailableA, unavailableB };

        foreach (var permutation in Permutations(providers))
        {
            var selected = Assert.IsType<ProcessContainmentBackendSelected>(
                ProcessContainmentBackendRouter.Select(
                    ProcessContainmentPlatform.Windows,
                    permutation.Select(Discover)));

            Assert.Same(proven, selected.Backend);
            Assert.Equal(proven.Identity, selected.Capability.BackendIdentity);
            Assert.All(selected.Capability.Evidence, evidence =>
                Assert.Equal(
                    ProcessContainmentCapabilityState.Proven,
                    evidence.State));
        }
    }

    [Fact]
    public void ZeroProvenBackendsFailClosed()
    {
        var unavailable = Backend(
            "windows-unavailable",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                []),
            ProcessContainmentSelectionFailureKind.NoProvenBackend);
        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                [Discover(unavailable)]),
            ProcessContainmentSelectionFailureKind.NoProvenBackend,
            "windows-unavailable");
    }

    [Fact]
    public void MultipleProvenBackendsFailClosedIndependentOfProviderOrder()
    {
        var first = Backend(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);
        var second = Backend(
            "linux-process-group",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);
        var forward = new[] { Discover(first), Discover(second) };

        var forwardFailure = AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                forward),
            ProcessContainmentSelectionFailureKind.MultipleProvenBackends,
            "linux-cgroup",
            "linux-process-group");
        var reverseFailure = AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                forward.Reverse()),
            ProcessContainmentSelectionFailureKind.MultipleProvenBackends,
            "linux-cgroup",
            "linux-process-group");

        Assert.Equal(forwardFailure.Kind, reverseFailure.Kind);
        Assert.Equal(forwardFailure.Detail, reverseFailure.Detail);
        Assert.Equal(
            forwardFailure.BackendIdentities,
            reverseFailure.BackendIdentities);
    }

    [Fact]
    public void UnknownCapabilityBlocksAProvenFallback()
    {
        var unknown = Backend(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Unknown);
        var fallback = Backend(
            "linux-process-group",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                [Discover(fallback), Discover(unknown)]),
            ProcessContainmentSelectionFailureKind.UnknownCapability,
            "linux-cgroup");
    }

    [Fact]
    public void EmptyCapabilityEvidenceIsUnknownAndFailsClosed()
    {
        var backend = Backend(
            "mac-process-group",
            ProcessContainmentPlatform.MacOS);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.MacOS,
                [Discover(backend)]),
            ProcessContainmentSelectionFailureKind.UnknownCapability,
            "mac-process-group");
    }

    [Fact]
    public void PlatformMismatchFailsClosedBeforeSelection()
    {
        var backend = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            capabilityPlatform: ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                [Discover(backend)]),
            ProcessContainmentSelectionFailureKind.BackendPlatformMismatch,
            "windows-job");
    }

    [Fact]
    public void DuplicateBackendIdentityFailsClosedEvenWhenEvidenceAgrees()
    {
        var first = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);
        var duplicate = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                [Discover(first), Discover(duplicate)]),
            ProcessContainmentSelectionFailureKind.DuplicateBackendIdentity,
            "windows-job");
    }

    [Fact]
    public void ContradictoryCapabilityEvidenceFailsClosed()
    {
        var backend = Backend(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven,
            ProcessContainmentCapabilityState.Unavailable);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                [Discover(backend)]),
            ProcessContainmentSelectionFailureKind.ContradictoryCapabilityEvidence,
            "linux-cgroup");
    }

    [Fact]
    public void CapabilityIdentityMismatchFailsClosed()
    {
        var backend = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            capabilityIdentity: "different-backend",
            ProcessContainmentCapabilityState.Proven);

        AssertRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                [Discover(backend)]),
            ProcessContainmentSelectionFailureKind.BackendIdentityMismatch,
            "different-backend",
            "windows-job");
    }

    [Fact]
    public void ProvidersForOtherPlatformsDoNotCompeteWithRequestedPlatform()
    {
        var windows = Backend(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);
        var linux = Backend(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);
        var mac = Backend(
            "mac-process-group",
            ProcessContainmentPlatform.MacOS,
            ProcessContainmentCapabilityState.Proven);

        var selected = Assert.IsType<ProcessContainmentBackendSelected>(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.MacOS,
                [Discover(windows), Discover(mac), Discover(linux)]));

        Assert.Same(mac, selected.Backend);
    }

    [Fact]
    public void CapabilityDiscoverySnapshotsEvidenceBeforeRouting()
    {
        var identity = new ProcessContainmentBackendIdentity("windows-job");
        var mutable = new List<ProcessContainmentCapabilityEvidence>
        {
            Evidence(ProcessContainmentCapabilityState.Proven)
        };
        var capability = new ProcessContainmentCapabilityDiscovery(
            identity,
            ProcessContainmentPlatform.Windows,
            mutable);
        mutable.Clear();

        Assert.Equal(
            ProcessContainmentCapabilityState.Proven,
            Assert.Single(capability.Evidence).State);
    }

    private static ProcessContainmentSelectionFailure AssertRejected(
        ProcessContainmentBackendSelectionResult result,
        ProcessContainmentSelectionFailureKind expectedKind,
        params string[] expectedIdentities)
    {
        var failure = Assert.IsType<ProcessContainmentBackendRejected>(result).Failure;
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(
            expectedIdentities,
            failure.BackendIdentities.Select(identity => identity.Value));
        return failure;
    }

    private static ProcessContainmentBackendDiscovery Discover(
        FixtureBackend backend)
    {
        return new ProcessContainmentBackendDiscovery(
            backend,
            backend.DiscoverCapability());
    }

    private static FixtureBackend Backend(
        string identity,
        ProcessContainmentPlatform platform,
        params ProcessContainmentCapabilityState[] states)
    {
        return Backend(identity, platform, null, null, states);
    }

    private static FixtureBackend Backend(
        string identity,
        ProcessContainmentPlatform platform,
        ProcessContainmentPlatform capabilityPlatform,
        params ProcessContainmentCapabilityState[] states)
    {
        return Backend(identity, platform, null, capabilityPlatform, states);
    }

    private static FixtureBackend Backend(
        string identity,
        ProcessContainmentPlatform platform,
        string capabilityIdentity,
        params ProcessContainmentCapabilityState[] states)
    {
        return Backend(identity, platform, capabilityIdentity, null, states);
    }

    private static FixtureBackend Backend(
        string identity,
        ProcessContainmentPlatform platform,
        string? capabilityIdentity,
        ProcessContainmentPlatform? capabilityPlatform,
        params ProcessContainmentCapabilityState[] states)
    {
        var backendIdentity = new ProcessContainmentBackendIdentity(identity);
        var discovery = new ProcessContainmentCapabilityDiscovery(
            new ProcessContainmentBackendIdentity(capabilityIdentity ?? identity),
            capabilityPlatform ?? platform,
            states.Select(Evidence));
        return new FixtureBackend(backendIdentity, platform, discovery);
    }

    private static ProcessContainmentCapabilityEvidence Evidence(
        ProcessContainmentCapabilityState state)
    {
        return new ProcessContainmentCapabilityEvidence(
            state,
            $"fixture {state}");
    }

    private static IEnumerable<IReadOnlyList<FixtureBackend>> Permutations(
        IReadOnlyList<FixtureBackend> providers)
    {
        for (var first = 0; first < providers.Count; first++)
        {
            for (var second = 0; second < providers.Count; second++)
            {
                if (second == first)
                {
                    continue;
                }

                var third = Enumerable.Range(0, providers.Count)
                    .Single(index => index != first && index != second);
                yield return
                [
                    providers[first],
                    providers[second],
                    providers[third]
                ];
            }
        }
    }

    private sealed class FixtureBackend : IProcessContainmentBackend
    {
        private readonly ProcessContainmentCapabilityDiscovery _capability;

        internal FixtureBackend(
            ProcessContainmentBackendIdentity identity,
            ProcessContainmentPlatform platform,
            ProcessContainmentCapabilityDiscovery capability)
        {
            Identity = identity;
            Platform = platform;
            _capability = capability;
        }

        public ProcessContainmentBackendIdentity Identity { get; }

        public ProcessContainmentPlatform Platform { get; }

        public ProcessContainmentCapabilityDiscovery DiscoverCapability()
        {
            return _capability;
        }
    }
}
