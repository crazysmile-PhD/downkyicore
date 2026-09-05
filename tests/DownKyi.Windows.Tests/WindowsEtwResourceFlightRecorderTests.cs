using System.Diagnostics;
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
    public void DiagnosticToolTimeoutPrecedesPipeCompletion()
    {
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<TimeoutException>(
            () => WindowsEtwResourceFlightRecorder.RunTool(
                "pwsh.exe",
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(2),
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.Write('started'); Start-Sleep -Seconds 30"));

        Assert.Contains("diagnostic timeout", exception.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TestTimeout);
    }
}
