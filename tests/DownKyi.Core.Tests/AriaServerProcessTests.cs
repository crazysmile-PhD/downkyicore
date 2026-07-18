using System.Diagnostics;
using DownKyi.Core.Aria2cNet.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class AriaServerProcessTests
{
    [Fact]
    public async Task WindowsLifetimeJobTerminatesTheAssignedProcessWhenReleased()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartLongRunningProcess();
        var processJob = WindowsProcessJob.TryCreateAndAssign(
            process,
            NullLogger.Instance);

        Assert.NotNull(processJob);
        processJob.Dispose();
        await process
            .WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void StartArgumentsBindAriaLifetimeToTheParentAndPreserveResumeFiles()
    {
        var config = new AriaConfig
        {
            ListenPort = 35076,
            Token = "test-token",
            LogLevel = AriaConfigLogLevel.WARN,
            MaxConcurrentDownloads = 3,
            MaxConnectionPerServer = 8,
            Split = 5,
            MinSplitSize = 10,
            ContinueDownload = true,
            FileAllocation = AriaConfigFileAllocation.NONE,
            Headers = ["Origin: https://www.bilibili.com"]
        };

        var arguments = AriaServer.BuildArguments(
            config,
            "aria.session",
            "aria.log",
            saveSessionInterval: 120,
            parentProcessId: 4242);

        Assert.Contains("--stop-with-process=4242", arguments, StringComparison.Ordinal);
        Assert.Contains("--rpc-listen-all=false", arguments, StringComparison.Ordinal);
        Assert.Contains("--rpc-allow-origin-all=false", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--rpc-listen-all=true", arguments, StringComparison.Ordinal);
        Assert.Contains("--input-file=\"aria.session\"", arguments, StringComparison.Ordinal);
        Assert.Contains("--save-session=\"aria.session\"", arguments, StringComparison.Ordinal);
        Assert.Contains("--continue=true", arguments, StringComparison.Ordinal);
        Assert.Contains("--header=\"Origin: https://www.bilibili.com\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillTrackedServerTerminatesAndReleasesTrackedProcess()
    {
        var server = new AriaServer(NullLoggerFactory.Instance);
        using var process = StartLongRunningProcess();
        server.SetTrackedServerForTests(process);

        try
        {
            Assert.True(server.KillTrackedServer("test cleanup"));
            await process
                .WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(process.HasExited);
            Assert.False(server.HasTrackedServerForTests());
        }
        finally
        {
            server.SetTrackedServerForTests(null);
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
