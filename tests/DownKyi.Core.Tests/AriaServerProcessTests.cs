using System.Diagnostics;
using DownKyi.Core.Aria2cNet.Server;

namespace DownKyi.Core.Tests;

public sealed class AriaServerProcessTests
{
    [Fact]
    public async Task KillTrackedServerTerminatesAndReleasesTrackedProcess()
    {
        using var process = StartLongRunningProcess();
        AriaServer.SetTrackedServerForTests(process);

        try
        {
            Assert.True(AriaServer.KillTrackedServer("test cleanup"));
            await process
                .WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(process.HasExited);
            Assert.False(AriaServer.HasTrackedServerForTests());
        }
        finally
        {
            AriaServer.SetTrackedServerForTests(null);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(
                "powershell.exe",
                "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("/bin/sh", "-c \"exec sleep 30\"");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Could not start the process used by the cleanup test.");
    }
}
