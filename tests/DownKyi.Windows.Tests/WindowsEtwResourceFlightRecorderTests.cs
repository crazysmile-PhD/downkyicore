using System.Diagnostics;
using DownKyi.CentralTestRunner;
using DownKyi.TestInfrastructure;

namespace DownKyi.Windows.Tests;

public sealed class WindowsEtwResourceFlightRecorderTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void DiagnosticToolDrainsStandardOutputAndErrorConcurrently()
    {
        var result = WindowsEtwResourceFlightRecorder.RunTool(
            "pwsh.exe",
            TestTimeout,
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
                    TimeSpan.FromSeconds(3),
                    "exec",
                    "--runtimeconfig",
                    runtimeConfig,
                    typeof(Program).Assembly.Location,
                    "fixture-exit-with-pipe-holder",
                    runtimeConfig,
                    marker));

            Assert.Contains("output did not drain", exception.Message, StringComparison.Ordinal);
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
