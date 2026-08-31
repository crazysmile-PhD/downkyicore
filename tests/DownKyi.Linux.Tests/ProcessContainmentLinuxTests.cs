using System.Diagnostics;
using DownKyi.ProcessSupervision;

namespace DownKyi.Linux.Tests;

public sealed class ProcessContainmentLinuxTests
{
    [Fact]
    public void CurrentCapabilitySelectsExactlyOneSupportedBackend()
    {
        var capability = LinuxCgroupCapabilityProbe.Probe();
        Assert.NotEqual(LinuxCgroupAvailability.Ambiguous, capability.Availability);
        var facts = new PlatformContainmentFacts(
            ProcessContainmentPlatform.Linux,
            capability);

        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);

        Assert.Equal(
            capability.Availability == LinuxCgroupAvailability.WritableDelegation
                ? ProcessContainmentBackendKind.LinuxDelegatedCgroup
                : ProcessContainmentBackendKind.LinuxProcessGroup,
            backend.Kind);
    }

    [Fact]
    public void WritableDelegationCanPrepareAndRollBackARealStagedCgroup()
    {
        var capability = LinuxCgroupCapabilityProbe.Probe();
        if (capability.Availability != LinuxCgroupAvailability.WritableDelegation)
        {
            return;
        }

        var facts = new PlatformContainmentFacts(
            ProcessContainmentPlatform.Linux,
            capability);
        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);
        using var current = Process.GetCurrentProcess();
        using var lease = backend.Prepare(current, facts);

        Assert.Equal(ProcessContainmentKind.LinuxCgroupV2, lease.Metadata.ContainmentKind);
        Assert.Equal(
            ProcessContainmentStrength.DelegatedCgroupTree,
            lease.Metadata.ContainmentStrength);
        Assert.Equal(QuiescenceObservationPoint.BeforeAnchorReap, lease.ObservationPoint);
    }

    [Fact]
    public void ProcessGroupQuiescenceRejectsObservationBeforeAnchorReap()
    {
        using var current = Process.GetCurrentProcess();
        using var lease = new LinuxProcessGroupContainmentLease(current.Id);

        var failure = Assert.Throws<ContainmentAuthorityException>(
            () => lease.ObserveQuiescence());

        Assert.Equal(ContainmentAuthorityFailureKind.InvalidAnchorState, failure.Kind);
    }
}
