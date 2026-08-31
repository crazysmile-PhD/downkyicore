using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class ProcessContainmentContractTests
{
    [Fact]
    public void WindowsJobMembershipClassifiesRetainedInfrastructureAndDescendants()
    {
        nuint anchor = 10;
        nuint consoleHost = 11;
        nuint target = 20;
        Assert.Equal(
            ContainmentOccupancy.Quiescent,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: false,
                [anchor, consoleHost],
                [anchor, consoleHost],
                anchor));
        Assert.Equal(
            ContainmentOccupancy.Quiescent,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: false,
                [anchor],
                [anchor, consoleHost],
                anchor));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: false,
                [anchor, consoleHost, target],
                [anchor, consoleHost],
                anchor));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: true,
                [anchor],
                [anchor],
                anchor));
        Assert.Equal(
            ContainmentOccupancy.Quiescent,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: true,
                [],
                [],
                anchor,
                terminationRequested: true));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: true,
                [target],
                [],
                anchor,
                terminationRequested: true));

        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            WindowsJobContainmentLease.ClassifyActiveMembersForTesting(
                anchorHasExited: false,
                [consoleHost],
                [anchor, consoleHost],
                anchor));
        Assert.Equal(ContainmentAuthorityFailureKind.MembershipAmbiguous, failure.Kind);
    }

    [Theory]
    [InlineData(2u, 3u, 2u)]
    [InlineData(2u, 2u, 1u)]
    [InlineData(2u, 1u, 1u)]
    public void WindowsJobSnapshotCountChurnFailsClosed(
        uint expectedActiveProcesses,
        uint assignedProcesses,
        uint listedProcesses)
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            WindowsJobContainmentLease.ValidateSnapshotCountsForTesting(
                expectedActiveProcesses,
                assignedProcesses,
                listedProcesses));

        Assert.Equal(ContainmentAuthorityFailureKind.MembershipAmbiguous, failure.Kind);
    }

    [Fact]
    public void WindowsJobSameCountMembershipChurnFailsClosed()
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            WindowsJobContainmentLease.ValidateStableInfrastructureSnapshotForTesting(
                new nuint[] { 10, 11 },
                new nuint[] { 10, 12 }));

        Assert.Equal(ContainmentAuthorityFailureKind.MembershipAmbiguous, failure.Kind);
        WindowsJobContainmentLease.ValidateStableInfrastructureSnapshotForTesting(
            new nuint[] { 10, 11 },
            new nuint[] { 11, 10 });
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-1, 3, false)]
    [InlineData(-1, 1, true)]
    public void AcceptedTerminationRequestsRemainSubordinateToOccupancy(
        int result,
        int error,
        bool allowDarwinPermissionDenied)
    {
        PosixProcessGroupNative.ValidateTerminationRequestResult(
            result,
            error,
            allowDarwinPermissionDenied);
    }

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(-1, 5, true)]
    public void AmbiguousTerminationRequestsFailClosed(
        int result,
        int error,
        bool allowDarwinPermissionDenied)
    {
        var failure = Assert.Throws<ContainmentAuthorityException>(() =>
            PosixProcessGroupNative.ValidateTerminationRequestResult(
                result,
                error,
                allowDarwinPermissionDenied));

        Assert.Equal(ContainmentAuthorityFailureKind.OperationFailed, failure.Kind);
    }

    [Fact]
    public void UnifiedCgroupMembershipRequiresExactlyOneAbsoluteEntry()
    {
        Assert.Equal(
            "/delegated.scope",
            LinuxCgroupCapabilityProbe.ParseUnifiedMembership(["0::/delegated.scope"]));

        Assert.Throws<ContainmentAuthorityException>(() =>
            LinuxCgroupCapabilityProbe.ParseUnifiedMembership([]));
        Assert.Throws<ContainmentAuthorityException>(() =>
            LinuxCgroupCapabilityProbe.ParseUnifiedMembership(["0::relative"]));
        Assert.Throws<ContainmentAuthorityException>(() =>
            LinuxCgroupCapabilityProbe.ParseUnifiedMembership(["0::/a", "0::/b"]));
    }

    [Fact]
    public void MacMembershipClassifierRequiresExactlyOneRetainedAnchor()
    {
        const int anchor = 41;

        Assert.Equal(
            ContainmentOccupancy.Quiescent,
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                anchor,
                [anchor]));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                anchor,
                [anchor, 42]));

        foreach (var ambiguous in new[]
                 {
                     Array.Empty<int>(),
                     new[] { 42 },
                     new[] { anchor, 0 },
                     new[] { anchor, anchor }
                 })
        {
            var failure = Assert.Throws<ContainmentAuthorityException>(() =>
                MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                    anchor,
                    ambiguous));
            Assert.Equal(ContainmentAuthorityFailureKind.MembershipAmbiguous, failure.Kind);
        }

        var invalidAnchor = Assert.Throws<ContainmentAuthorityException>(() =>
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                0,
                [anchor]));
        Assert.Equal(
            ContainmentAuthorityFailureKind.MembershipAmbiguous,
            invalidAnchor.Kind);

        Assert.Equal(
            ContainmentOccupancy.Quiescent,
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                anchor,
                [],
                terminationRequested: true));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                anchor,
                [42],
                terminationRequested: true));
        Assert.Equal(
            ContainmentOccupancy.Occupied,
            MacProcessGroupContainmentLease.ClassifyMembershipSnapshotForTesting(
                anchor,
                [anchor],
                terminationRequested: true));
    }

    [Fact]
    public void MacMembershipCapacityReservesOneBoundedSnapshotHeadroom()
    {
        var capacity = MacProcessGroupContainmentLease.CalculateSnapshotCapacityForTesting(32);

        Assert.True(capacity > 32);
        MacProcessGroupContainmentLease.ValidateSnapshotCountForTesting(32, capacity);
        MacProcessGroupContainmentLease.ValidateSnapshotCountForTesting(
            capacity - 1,
            capacity);
        var fullFailure = Assert.Throws<ContainmentAuthorityException>(() =>
            MacProcessGroupContainmentLease.ValidateSnapshotCountForTesting(
                capacity,
                capacity));
        Assert.Equal(
            ContainmentAuthorityFailureKind.MembershipAmbiguous,
            fullFailure.Kind);
        Assert.Throws<ContainmentAuthorityException>(() =>
            MacProcessGroupContainmentLease.CalculateSnapshotCapacityForTesting(int.MaxValue));
    }

    [Fact]
    public void MissingLinuxCgroupV2AuthoritySelectsProcessGroupFallback()
    {
        foreach (var snapshot in new[]
                 {
                     Snapshot([]),
                     Snapshot(["0::/fixture"], mount: LinuxCgroupAuthorityPresence.Missing),
                     Snapshot(
                         ["0::/fixture"],
                         mount: LinuxCgroupAuthorityPresence.Missing,
                         directory: LinuxCgroupAuthorityPresence.Ambiguous),
                     Snapshot(["0::/fixture"], kill: LinuxCgroupAuthorityPresence.Missing),
                     Snapshot(["0::/fixture"], directory: LinuxCgroupAuthorityPresence.Missing),
                     Snapshot(["0::/fixture"], accessError: 13),
                     Snapshot(["0::/fixture"], accessError: 30)
                 })
        {
            var capability = LinuxCgroupCapabilityProbe.ClassifyForTesting(snapshot);
            Assert.Equal(
                LinuxCgroupAvailability.DefinitelyUnavailable,
                capability.Availability);
            var backend = PlatformProcessContainmentRouter.Select(
                new PlatformContainmentFacts(
                    ProcessContainmentPlatform.Linux,
                    capability),
                ProcessContainmentRequirement.AllowWeakerFallback);
            Assert.Equal(ProcessContainmentBackendKind.LinuxProcessGroup, backend.Kind);
        }
    }

    [Fact]
    public void MalformedOrUnknownLinuxCgroupAuthorityFailsClosed()
    {
        foreach (var snapshot in new[]
                 {
                     Snapshot(["0::relative"]),
                     Snapshot(["0::/a", "0::/b"]),
                     Snapshot(
                         ["0::/fixture"],
                         mount: LinuxCgroupAuthorityPresence.Ambiguous),
                     Snapshot(["0::/fixture"], accessError: 5)
                 })
        {
            var capability = LinuxCgroupCapabilityProbe.ClassifyForTesting(snapshot);
            Assert.Equal(LinuxCgroupAvailability.Ambiguous, capability.Availability);
            var failure = Assert.Throws<ContainmentAuthorityException>(() =>
                PlatformProcessContainmentRouter.Select(
                    new PlatformContainmentFacts(
                        ProcessContainmentPlatform.Linux,
                        capability),
                    ProcessContainmentRequirement.AllowWeakerFallback));
            Assert.Equal(ContainmentAuthorityFailureKind.AmbiguousCapability, failure.Kind);
        }
    }

    [Fact]
    public void CompleteWritableLinuxCgroupAuthoritySelectsCgroup()
    {
        var capability = LinuxCgroupCapabilityProbe.ClassifyForTesting(
            Snapshot(["0::/fixture"], accessResult: 0, accessError: 0));

        Assert.Equal(LinuxCgroupAvailability.WritableDelegation, capability.Availability);
        Assert.Equal(
            ProcessContainmentBackendKind.LinuxDelegatedCgroup,
            PlatformProcessContainmentRouter.Select(
                new PlatformContainmentFacts(
                    ProcessContainmentPlatform.Linux,
                    capability),
                ProcessContainmentRequirement.AllowWeakerFallback).Kind);
    }

    private static LinuxCgroupProbeSnapshot Snapshot(
        IReadOnlyList<string> membershipLines,
        LinuxCgroupAuthorityPresence mount = LinuxCgroupAuthorityPresence.Present,
        LinuxCgroupAuthorityPresence directory = LinuxCgroupAuthorityPresence.Present,
        LinuxCgroupAuthorityPresence processMembership = LinuxCgroupAuthorityPresence.Present,
        LinuxCgroupAuthorityPresence occupancy = LinuxCgroupAuthorityPresence.Present,
        LinuxCgroupAuthorityPresence kill = LinuxCgroupAuthorityPresence.Present,
        int accessResult = -1,
        int accessError = 13)
    {
        return new LinuxCgroupProbeSnapshot(
            membershipLines,
            mount,
            directory,
            processMembership,
            occupancy,
            kill,
            "/sys/fs/cgroup/fixture",
            accessResult,
            accessError);
    }
}
