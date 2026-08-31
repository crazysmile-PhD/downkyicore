using System.Diagnostics;
using DownKyi.ProcessSupervision;

namespace DownKyi.MacOS.Tests;

public sealed class ProcessContainmentMacTests
{
    [Fact]
    public void LibprocObservesTheCurrentRealProcessGroup()
    {
        var processGroupId = PosixProcessGroupNative.GetCurrentProcessGroupId();
        var members = MacProcessGroupContainmentLease.QueryMembersForTesting(processGroupId);

        Assert.Contains(Environment.ProcessId, members);
    }

    [Fact]
    public void ReapedAnchorCannotProduceMacOSQuiescenceProof()
    {
        using var current = Process.GetCurrentProcess();
        using var lease = new MacProcessGroupContainmentLease(current.Id);
        lease.MarkAnchorReaped();

        var failure = Assert.Throws<ContainmentAuthorityException>(
            () => lease.ObserveQuiescence());

        Assert.Equal(ContainmentAuthorityFailureKind.InvalidAnchorState, failure.Kind);
    }
}
