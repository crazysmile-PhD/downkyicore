using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessContainmentRoutingBehaviorTests
{
    [Theory]
    [InlineData("Windows-job")]
    [InlineData("windows\u200B-job")]
    [InlineData("caf\u00E9")]
    [InlineData("cafe\u0301")]
    [InlineData("-windows-job")]
    [InlineData("windows-job-")]
    [InlineData("windows--job")]
    [InlineData("windows_job")]
    public void BackendIdentityRejectsNonCanonicalTokens(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            new ProcessContainmentBackendIdentity(value));
    }

    [Fact]
    public void CoordinatorInvokesProvidersInCanonicalOrderBeforeRouting()
    {
        var forward = RunOrderSensitiveDiscovery(reverse: false);
        var reverse = RunOrderSensitiveDiscovery(reverse: true);

        Assert.Equal(["alpha-backend", "beta-backend"], forward.Invocations);
        Assert.Equal(forward.Invocations, reverse.Invocations);
        Assert.Equal("alpha-backend", forward.Selected.BackendIdentity.Value);
        Assert.Equal(
            forward.Selected.BackendIdentity,
            reverse.Selected.BackendIdentity);
    }

    [Fact]
    public void RegistrationEnumerationFailureIsTypedBeforeProviderInvocation()
    {
        var providerInvoked = false;
        var registration = Registration(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            () =>
            {
                providerInvoked = true;
                return Report(ProcessContainmentCapabilityState.Proven);
            });

        var result = ProcessContainmentCapabilityDiscoveryCoordinator.Discover(
            ThrowAfterFirst(registration));

        var failure = AssertDiscoveryRejected(
            result,
            ProcessContainmentCapabilityDiscoveryFailureKind.RegistrationEnumerationFailed);
        Assert.Equal(nameof(InvalidOperationException), failure.ErrorType);
        Assert.False(providerInvoked);
    }

    [Fact]
    public void CapabilityProviderFailureIsTyped()
    {
        var registration = Registration(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            () => throw new InvalidOperationException("fixture discovery failure"));

        var failure = AssertDiscoveryRejected(
            ProcessContainmentCapabilityDiscoveryCoordinator.Discover([registration]),
            ProcessContainmentCapabilityDiscoveryFailureKind.CapabilityProviderFailed,
            "windows-job");

        Assert.Equal(nameof(InvalidOperationException), failure.ErrorType);
        Assert.DoesNotContain(
            "fixture discovery failure",
            failure.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedBackendUsesFrozenDescriptorInsteadOfLiveHandleMetadata()
    {
        var handle = new FixtureBackend
        {
            LiveIdentity = "windows-job",
            LivePlatform = ProcessContainmentPlatform.Windows
        };
        var registration = Registration(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            () => Report(ProcessContainmentCapabilityState.Proven),
            handle);
        var selected = Assert.IsType<ProcessContainmentBackendSelected>(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                DiscoverBatch(registration)));

        handle.LiveIdentity = "linux-cgroup";
        handle.LivePlatform = ProcessContainmentPlatform.Linux;

        Assert.Equal("windows-job", selected.BackendIdentity.Value);
        Assert.Equal(ProcessContainmentPlatform.Windows, selected.Platform);
        Assert.Same(handle, selected.ExecutionHandle);
        Assert.Equal(
            ProcessContainmentCapabilityState.Proven,
            Assert.Single(selected.Capability.Evidence).State);
    }

    [Fact]
    public void ExactlyOneProvenBackendIsSelectedWithUnavailableAlternatives()
    {
        var proven = Registration(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);
        var unavailableA = Registration(
            "windows-alt-a",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);
        var unavailableB = Registration(
            "windows-alt-b",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);

        var selected = Assert.IsType<ProcessContainmentBackendSelected>(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                DiscoverBatch(unavailableB, proven, unavailableA)));

        Assert.Equal("windows-job", selected.BackendIdentity.Value);
    }

    [Fact]
    public void ZeroProvenBackendsFailClosed()
    {
        var unavailable = Registration(
            "windows-unavailable",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                DiscoverBatch()),
            ProcessContainmentSelectionFailureKind.NoProvenBackend);
        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                DiscoverBatch(unavailable)),
            ProcessContainmentSelectionFailureKind.NoProvenBackend,
            "windows-unavailable");
    }

    [Fact]
    public void MultipleProvenBackendsFailClosed()
    {
        var first = Registration(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);
        var second = Registration(
            "linux-process-group",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                DiscoverBatch(second, first)),
            ProcessContainmentSelectionFailureKind.MultipleProvenBackends,
            "linux-cgroup",
            "linux-process-group");
    }

    [Fact]
    public void UnknownCapabilityBlocksAProvenFallback()
    {
        var unknown = Registration(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Unknown);
        var fallback = Registration(
            "linux-process-group",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                DiscoverBatch(fallback, unknown)),
            ProcessContainmentSelectionFailureKind.UnknownCapability,
            "linux-cgroup");
    }

    [Fact]
    public void EmptyCapabilityEvidenceIsUnknownAndFailsClosed()
    {
        var registration = Registration(
            "mac-process-group",
            ProcessContainmentPlatform.MacOS);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.MacOS,
                DiscoverBatch(registration)),
            ProcessContainmentSelectionFailureKind.UnknownCapability,
            "mac-process-group");
    }

    [Fact]
    public void DuplicateRegistrationIdentityFailsBeforeProviderInvocation()
    {
        var invocationCount = 0;
        ProcessContainmentBackendRegistration CreateDuplicate()
        {
            return Registration(
                "windows-job",
                ProcessContainmentPlatform.Windows,
                () =>
                {
                    invocationCount++;
                    return Report(ProcessContainmentCapabilityState.Proven);
                });
        }

        AssertDiscoveryRejected(
            ProcessContainmentCapabilityDiscoveryCoordinator.Discover(
                [CreateDuplicate(), CreateDuplicate()]),
            ProcessContainmentCapabilityDiscoveryFailureKind.DuplicateBackendIdentity,
            "windows-job");
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public void RouterRejectsDuplicateIdentityInSealedBatch()
    {
        var batch = new ProcessContainmentDiscoveryBatch(
        [
            Discovery(
                "windows-job",
                ProcessContainmentPlatform.Windows,
                ProcessContainmentCapabilityState.Proven),
            Discovery(
                "windows-job",
                ProcessContainmentPlatform.Windows,
                ProcessContainmentCapabilityState.Proven)
        ]);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                batch),
            ProcessContainmentSelectionFailureKind.DuplicateBackendIdentity,
            "windows-job");
    }

    [Fact]
    public void ContradictoryCapabilityEvidenceFailsClosed()
    {
        var registration = Registration(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven,
            ProcessContainmentCapabilityState.Unavailable);

        AssertSelectionRejected(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Linux,
                DiscoverBatch(registration)),
            ProcessContainmentSelectionFailureKind.ContradictoryCapabilityEvidence,
            "linux-cgroup");
    }

    [Fact]
    public void ProvidersForOtherPlatformsDoNotCompeteWithRequestedPlatform()
    {
        var windows = Registration(
            "windows-job",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Proven);
        var linux = Registration(
            "linux-cgroup",
            ProcessContainmentPlatform.Linux,
            ProcessContainmentCapabilityState.Proven);
        var mac = Registration(
            "mac-process-group",
            ProcessContainmentPlatform.MacOS,
            ProcessContainmentCapabilityState.Proven);

        var selected = Assert.IsType<ProcessContainmentBackendSelected>(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.MacOS,
                DiscoverBatch(windows, mac, linux)));

        Assert.Equal("mac-process-group", selected.BackendIdentity.Value);
        Assert.Equal(ProcessContainmentPlatform.MacOS, selected.Platform);
    }

    [Fact]
    public void InvalidRequestedPlatformHasOneTypedFailure()
    {
        var result = ProcessContainmentBackendRouter.Select(
            (ProcessContainmentPlatform)999,
            DiscoverBatch());

        AssertSelectionRejected(
            result,
            ProcessContainmentSelectionFailureKind.InvalidRequestedPlatform);
    }

    [Fact]
    public void CapabilityReportAndDiscoveryBatchSnapshotTheirInputs()
    {
        var evidence = new List<ProcessContainmentCapabilityEvidence>
        {
            Evidence(ProcessContainmentCapabilityState.Proven)
        };
        var capability = new ProcessContainmentCapabilityReport(evidence);
        evidence.Clear();
        var first = new ProcessContainmentBackendDiscovery(
            Identity("windows-job"),
            ProcessContainmentPlatform.Windows,
            new FixtureBackend(),
            capability);
        var mutableDiscoveries = new[] { first };
        var batch = new ProcessContainmentDiscoveryBatch(mutableDiscoveries);
        mutableDiscoveries[0] = Discovery(
            "replacement",
            ProcessContainmentPlatform.Windows,
            ProcessContainmentCapabilityState.Unavailable);

        Assert.Equal(
            ProcessContainmentCapabilityState.Proven,
            Assert.Single(capability.Evidence).State);
        Assert.Same(first, Assert.Single(batch.Discoveries));
    }

    private static OrderSensitiveResult RunOrderSensitiveDiscovery(bool reverse)
    {
        var invocationOrder = new List<string>();
        var counter = 0;
        ProcessContainmentBackendRegistration Create(string identity)
        {
            return Registration(
                identity,
                ProcessContainmentPlatform.Windows,
                () =>
                {
                    invocationOrder.Add(identity);
                    var state = counter++ == 0
                        ? ProcessContainmentCapabilityState.Proven
                        : ProcessContainmentCapabilityState.Unavailable;
                    return Report(state);
                });
        }

        var alpha = Create("alpha-backend");
        var beta = Create("beta-backend");
        var registrations = reverse
            ? new[] { beta, alpha }
            : new[] { alpha, beta };
        var batch = DiscoverBatch(registrations);
        var selected = Assert.IsType<ProcessContainmentBackendSelected>(
            ProcessContainmentBackendRouter.Select(
                ProcessContainmentPlatform.Windows,
                batch));
        return new OrderSensitiveResult(invocationOrder, selected);
    }

    private static ProcessContainmentCapabilityDiscoveryFailure AssertDiscoveryRejected(
        ProcessContainmentCapabilityDiscoveryResult result,
        ProcessContainmentCapabilityDiscoveryFailureKind expectedKind,
        params string[] expectedIdentities)
    {
        var failure = Assert.IsType<ProcessContainmentCapabilityDiscoveryRejected>(
            result).Failure;
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(
            expectedIdentities,
            failure.BackendIdentities.Select(identity => identity.Value));
        return failure;
    }

    private static ProcessContainmentSelectionFailure AssertSelectionRejected(
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

    private static ProcessContainmentDiscoveryBatch DiscoverBatch(
        params ProcessContainmentBackendRegistration[] registrations)
    {
        return Assert.IsType<ProcessContainmentCapabilityDiscoveryCompleted>(
            ProcessContainmentCapabilityDiscoveryCoordinator.Discover(registrations)).Batch;
    }

    private static ProcessContainmentBackendRegistration Registration(
        string identity,
        ProcessContainmentPlatform platform,
        params ProcessContainmentCapabilityState[] states)
    {
        return Registration(identity, platform, () => Report(states));
    }

    private static ProcessContainmentBackendRegistration Registration(
        string identity,
        ProcessContainmentPlatform platform,
        Func<ProcessContainmentCapabilityReport> discover,
        FixtureBackend? handle = null)
    {
        return new ProcessContainmentBackendRegistration(
            Identity(identity),
            platform,
            handle ?? new FixtureBackend(),
            new FixtureCapabilityProvider(discover));
    }

    private static ProcessContainmentBackendDiscovery Discovery(
        string identity,
        ProcessContainmentPlatform platform,
        params ProcessContainmentCapabilityState[] states)
    {
        return new ProcessContainmentBackendDiscovery(
            Identity(identity),
            platform,
            new FixtureBackend(),
            Report(states));
    }

    private static ProcessContainmentBackendIdentity Identity(string value)
    {
        return new ProcessContainmentBackendIdentity(value);
    }

    private static ProcessContainmentCapabilityReport Report(
        params ProcessContainmentCapabilityState[] states)
    {
        return new ProcessContainmentCapabilityReport(states.Select(Evidence));
    }

    private static ProcessContainmentCapabilityEvidence Evidence(
        ProcessContainmentCapabilityState state)
    {
        return new ProcessContainmentCapabilityEvidence(
            state,
            $"fixture {state}");
    }

    private static IEnumerable<ProcessContainmentBackendRegistration> ThrowAfterFirst(
        ProcessContainmentBackendRegistration registration)
    {
        yield return registration;
        throw new InvalidOperationException("fixture enumeration failure");
    }

    private sealed record OrderSensitiveResult(
        IReadOnlyList<string> Invocations,
        ProcessContainmentBackendSelected Selected);

    private sealed class FixtureBackend : IProcessContainmentBackend
    {
        internal string LiveIdentity { get; set; } = "fixture";

        internal ProcessContainmentPlatform LivePlatform { get; set; } =
            ProcessContainmentPlatform.Windows;
    }

    private sealed class FixtureCapabilityProvider : IProcessContainmentCapabilityProvider
    {
        private readonly Func<ProcessContainmentCapabilityReport> _discover;

        internal FixtureCapabilityProvider(
            Func<ProcessContainmentCapabilityReport> discover)
        {
            _discover = discover;
        }

        public ProcessContainmentCapabilityReport DiscoverCapability()
        {
            return _discover();
        }
    }
}
