using System.Diagnostics;
using DownKyi.ProcessSupervision;

namespace DownKyi.Windows.Tests;

public sealed class ProcessContainmentWindowsTests
{
    [Fact]
    public async Task JobTerminationReachesZeroWithoutDisposingCallerAnchor()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/q");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("set /p hold=");
        using var anchor = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The inert Windows test anchor did not start.");
        var facts = new PlatformContainmentFacts(
            ProcessContainmentPlatform.Windows,
            LinuxCgroupCapability.Ambiguous("not applicable"));
        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);
        using var lease = backend.Prepare(anchor, facts);

        try
        {
            lease.AttachAnchor(anchor);
            lease.AssertAnchorOwned(anchor);

            Assert.True(lease.Metadata.OwnershipEstablished);
            Assert.Equal(ProcessContainmentKind.WindowsJobObject, lease.Metadata.ContainmentKind);

            lease.Terminate();
            await anchor.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(ContainmentOccupancy.Quiescent, lease.ObserveQuiescence());
            lease.MarkAnchorReaped();
            var failure = Assert.Throws<ContainmentAuthorityException>(
                () => lease.ObserveQuiescence());
            Assert.Equal(ContainmentAuthorityFailureKind.InvalidAnchorState, failure.Kind);
            lease.Dispose();
            Assert.True(anchor.HasExited);
        }
        finally
        {
            if (!anchor.HasExited)
            {
                anchor.Kill(entireProcessTree: true);
                await anchor.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }
        }
    }

    [Fact]
    public void UnattachedCurrentProcessCannotClaimJobMembership()
    {
        using var current = Process.GetCurrentProcess();
        var facts = new PlatformContainmentFacts(
            ProcessContainmentPlatform.Windows,
            LinuxCgroupCapability.Ambiguous("not applicable"));
        var backend = PlatformProcessContainmentRouter.Select(
            facts,
            ProcessContainmentRequirement.AllowWeakerFallback);
        using var lease = backend.Prepare(current, facts);

        var failure = Assert.Throws<ContainmentAuthorityException>(
            () => backend.EstablishCurrentProcess(lease.Attachment));

        Assert.Equal(ContainmentAuthorityFailureKind.MembershipAmbiguous, failure.Kind);
    }
}
