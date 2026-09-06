using System.Diagnostics;
using DownKyi.CentralTestRunner;

namespace DownKyi.MacOS.Tests;

public sealed class MacOsProcessIdentityProbeTests
{
    [Fact]
    public void CurrentProcessHasCompleteLiveBsdIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var expectedStartTimeUtc = process.StartTime.ToUniversalTime();

        var result = MacOsProcessIdentityProbe.Probe(process.Id, expectedStartTimeUtc);

        Assert.Equal(MacOsProcessIdentityState.SameIdentityLive, result.State);
        Assert.Equal(136, result.BytesReturned);
        Assert.Equal(process.Id, result.ProcessId);
        Assert.NotNull(result.StartTimeUtc);
        Assert.Equal(expectedStartTimeUtc, result.StartTimeUtc);
    }
}
