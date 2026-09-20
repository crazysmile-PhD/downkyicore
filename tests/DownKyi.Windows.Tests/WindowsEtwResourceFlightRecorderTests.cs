using System.ComponentModel;
using System.Diagnostics;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;
using DownKyi.TestInfrastructure;

namespace DownKyi.Windows.Tests;

public sealed class WindowsEtwResourceFlightRecorderTests
{
    private static readonly TimeSpan DrainTestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PipeHolderTestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void DiagnosticToolDrainsStandardOutputAndErrorConcurrently()
    {
        var result = WindowsEtwResourceFlightRecorder.RunTool(
            "pwsh.exe",
            DrainTestTimeout,
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$payload = 'x' * (128 * 1024); " +
            "[Console]::Out.Write($payload); [Console]::Error.Write($payload)");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(256 * 1024, result.Output.Length);
    }

    [Fact]
    public void DiagnosticToolDeadlineIncludesTerminationAndOutputDrain()
    {
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<TimeoutException>(
            () => WindowsEtwResourceFlightRecorder.RunTool(
                "pwsh.exe",
                TimeSpan.FromSeconds(2),
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.Write('started'); Start-Sleep -Seconds 30"));

        Assert.Contains("diagnostic timeout", exception.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TestTimeout);
    }

    [Fact]
    public void DiagnosticToolOwnsPipeHoldingDescendantAfterRootExit()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"downkyi-etw-tool-child-{Guid.NewGuid():N}.pid");
        int? childPid = null;
        try
        {
            var runtimeConfig = Path.Combine(
                AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(WindowsEtwResourceFlightRecorderTests).Assembly.Location)}.runtimeconfig.json");

            var exception = Assert.Throws<TimeoutException>(
                () => WindowsEtwResourceFlightRecorder.RunTool(
                    "dotnet",
                    PipeHolderTestTimeout,
                    "exec",
                    "--runtimeconfig",
                    runtimeConfig,
                    typeof(Program).Assembly.Location,
                    "fixture-exit-with-pipe-holder",
                    runtimeConfig,
                    marker));

            Assert.Contains("diagnostic timeout", exception.Message, StringComparison.Ordinal);
            childPid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(IsAlive(childPid.Value));
        }
        finally
        {
            if (childPid is { } pid && IsAlive(pid))
            {
                using var child = Process.GetProcessById(pid);
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }

            File.Delete(marker);
        }
    }

    [Fact]
    public async Task SharedSupervisorAssignmentFailureIsReapedAndDoesNotLaunchTool()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"downkyi-etw-tool-start-{Guid.NewGuid():N}.txt");
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OwnedProcessScope.StartAsync(
                    CreateMarkerStartInfo(marker),
                    TestTimeout,
                    $"Local\\downkyi-missing-job-{Guid.NewGuid():N}")).ConfigureAwait(true);

            Assert.Contains("did not launch", failure.Message, StringComparison.Ordinal);
            var hostPid = Assert.IsType<int>(failure.Data[OwnedProcessScope.FailedHostPidDataKey]);
            Assert.False(IsAlive(hostPid));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task SharedSupervisorHonorsElapsedCallerDeadlineBeforeLaunch()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"downkyi-etw-expired-start-{Guid.NewGuid():N}.txt");
        try
        {
            var deadline = new CleanupDeadline(TimeSpan.FromMilliseconds(50));
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var stopwatch = Stopwatch.StartNew();

            var failure = await Assert.ThrowsAsync<TimeoutException>(
                () => OwnedProcessScope.StartAsync(CreateMarkerStartInfo(marker), deadline)).ConfigureAwait(true);

            Assert.Contains("deadline expired before launch", failure.Message, StringComparison.Ordinal);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task SharedSupervisorPreservesFailureBeforeHostStarts()
    {
        var missingHost = Path.Combine(
            Path.GetTempPath(),
            $"missing-supervision-host-{Guid.NewGuid():N}.exe");

        var failure = await Assert.ThrowsAsync<Win32Exception>(
            () => OwnedProcessScope.StartAsync(
                CreateMarkerStartInfo(Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}")),
                TestTimeout,
                hostJobNameOverride: null,
                hostExecutableOverride: missingHost)).ConfigureAwait(true);

        Assert.DoesNotContain("No process is associated", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessStartInfo CreateMarkerStartInfo(string marker)
    {
        var startInfo = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"Set-Content -LiteralPath '{marker.Replace("'", "''", StringComparison.Ordinal)}' -Value launched");
        return startInfo;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
