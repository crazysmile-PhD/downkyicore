using System.Diagnostics;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class ProcessContainmentRouterTests
{
    [Theory]
    [InlineData(
        (int)ProcessContainmentPlatform.Windows,
        (int)LinuxCgroupAvailability.Ambiguous,
        (int)ProcessContainmentBackendKind.WindowsJob)]
    [InlineData(
        (int)ProcessContainmentPlatform.MacOS,
        (int)LinuxCgroupAvailability.Ambiguous,
        (int)ProcessContainmentBackendKind.MacProcessGroup)]
    [InlineData(
        (int)ProcessContainmentPlatform.Linux,
        (int)LinuxCgroupAvailability.WritableDelegation,
        (int)ProcessContainmentBackendKind.LinuxDelegatedCgroup)]
    [InlineData(
        (int)ProcessContainmentPlatform.Linux,
        (int)LinuxCgroupAvailability.DefinitelyUnavailable,
        (int)ProcessContainmentBackendKind.LinuxProcessGroup)]
    public void SelectionUsesOnlyImmutableFacts(
        int platformValue,
        int availabilityValue,
        int expectedBackendValue)
    {
        var facts = Facts(
            (ProcessContainmentPlatform)platformValue,
            (LinuxCgroupAvailability)availabilityValue);

        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);

        Assert.Equal((ProcessContainmentBackendKind)expectedBackendValue, backend.Kind);
    }

    [Fact]
    public void AmbiguousLinuxAuthorityFailsClosed()
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            PlatformProcessContainmentRouter.Select(
                Facts(
                    ProcessContainmentPlatform.Linux,
                    LinuxCgroupAvailability.Ambiguous),
                ProcessContainmentRequirement.AllowWeakerFallback));

        Assert.Equal(ContainmentAuthorityFailureKind.AmbiguousCapability, failure.Kind);
    }

    [Fact]
    public void UnsupportedPlatformFailsClosed()
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            PlatformProcessContainmentRouter.Select(
                Facts(
                    ProcessContainmentPlatform.Unsupported,
                    LinuxCgroupAvailability.Ambiguous),
                ProcessContainmentRequirement.AllowWeakerFallback));

        Assert.Equal(ContainmentAuthorityFailureKind.UnsupportedPlatform, failure.Kind);
    }

    [Fact]
    public void SelectedCgroupPreparationFailureCannotDowngrade()
    {
        var facts = new PlatformContainmentFacts(
            ProcessContainmentPlatform.Linux,
            new LinuxCgroupCapability(
                LinuxCgroupAvailability.WritableDelegation,
                "/claimed-delegation",
                Path.GetTempPath(),
                null));
        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);
        using var current = Process.GetCurrentProcess();

        var failure = Assert.Throws<ContainmentAuthorityException>(
            () => backend.Prepare(current, facts));

        Assert.Equal(ProcessContainmentBackendKind.LinuxDelegatedCgroup, backend.Kind);
        Assert.Equal(ContainmentAuthorityFailureKind.AmbiguousCapability, failure.Kind);
    }

    [Fact]
    public void LinuxFallbackMetadataDeclaresTrustedKernelProcessGroupAuthority()
    {
        var facts = Facts(
            ProcessContainmentPlatform.Linux,
            LinuxCgroupAvailability.DefinitelyUnavailable);
        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);
        using var current = Process.GetCurrentProcess();
        using var lease = backend.Prepare(current, facts);

        Assert.Equal(ProcessContainmentKind.LinuxProcessGroup, lease.Metadata.ContainmentKind);
        Assert.Equal(
            ProcessContainmentStrength.TrustedChildProcessGroup,
            lease.Metadata.ContainmentStrength);
        Assert.Equal(
            ProcessMembershipAuthority.LinuxProcessGroupSignal,
            lease.Metadata.MembershipAuthority);
        Assert.Equal(QuiescenceObservationPoint.AfterAnchorReap, lease.ObservationPoint);
        Assert.False(lease.Metadata.OwnershipEstablished);
    }

    [Theory]
    [InlineData((int)ProcessContainmentPlatform.Linux)]
    [InlineData((int)ProcessContainmentPlatform.MacOS)]
    public void StrongRequirementRejectsWeakerFallback(int platformValue)
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            PlatformProcessContainmentRouter.Select(
                Facts(
                    (ProcessContainmentPlatform)platformValue,
                    LinuxCgroupAvailability.DefinitelyUnavailable),
                ProcessContainmentRequirement.RequireStrongContainment));

        Assert.Equal(ContainmentAuthorityFailureKind.AuthorityUnavailable, failure.Kind);
    }

    private static PlatformContainmentFacts Facts(
        ProcessContainmentPlatform platform,
        LinuxCgroupAvailability availability)
    {
        var capability = availability switch
        {
            LinuxCgroupAvailability.DefinitelyUnavailable =>
                LinuxCgroupCapability.DefinitelyUnavailable("fixture unavailable"),
            LinuxCgroupAvailability.Ambiguous =>
                LinuxCgroupCapability.Ambiguous("fixture ambiguous"),
            _ => new LinuxCgroupCapability(
                LinuxCgroupAvailability.WritableDelegation,
                "/fixture",
                "/sys/fs/cgroup/fixture",
                null)
        };
        return new PlatformContainmentFacts(platform, capability);
    }
}
