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
            "dotnet",
            TimeSpan.FromSeconds(10),
            CreateFixtureArguments("fixture-dual-output"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(256 * 1024, result.Output.Length);
    }

    [Fact]
    public void DiagnosticToolExecutionAndCleanupShareABoundedDeadline()
    {
        var clock = Stopwatch.StartNew();

        var exception = Record.Exception(
            () => WindowsEtwResourceFlightRecorder.RunTool(
                "dotnet",
                TimeSpan.FromSeconds(1),
                CreateFixtureArguments("fixture-hold")));

        var timeout = exception switch
        {
            TimeoutException directTimeout => directTimeout,
            AggregateException aggregate =>
                Assert.IsType<TimeoutException>(aggregate.InnerExceptions[0]),
            _ => throw new Xunit.Sdk.XunitException(
                "Expected a bounded timeout failure.")
        };
        Assert.Contains("diagnostic timeout", timeout.Message, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TestTimeout);
    }

    [Fact]
    public void DiagnosticToolTerminatesPipeHoldingDescendantAfterRootExit()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-etw-tool-child-{Guid.NewGuid():N}.pid");
        int? childPid = null;
        try
        {
            var clock = Stopwatch.StartNew();
            var exception = Record.Exception(
                () => WindowsEtwResourceFlightRecorder.RunTool(
                    "dotnet",
                    TimeSpan.FromSeconds(2),
                    CreateFixtureArguments(
                        "fixture-exit-with-pipe-holder",
                        RuntimeConfigPath,
                        marker)));

            var timeout = exception switch
            {
                TimeoutException directTimeout => directTimeout,
                AggregateException aggregate =>
                    Assert.IsType<TimeoutException>(aggregate.InnerExceptions[0]),
                _ => throw new Xunit.Sdk.XunitException(
                    "Expected a bounded timeout failure.")
            };
            Assert.Contains("diagnostic timeout", timeout.Message, StringComparison.Ordinal);
            Assert.InRange(clock.Elapsed, TimeSpan.Zero, TestTimeout);
            childPid = int.Parse(
                File.ReadAllText(marker),
                System.Globalization.CultureInfo.InvariantCulture);
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

    private static string RuntimeConfigPath => Path.Combine(
        AppContext.BaseDirectory,
        $"{Path.GetFileNameWithoutExtension(typeof(WindowsEtwResourceFlightRecorderTests).Assembly.Location)}.runtimeconfig.json");

    private static string[] CreateFixtureArguments(string mode, params string[] arguments)
    {
        var result = new List<string>
        {
            "exec",
            "--runtimeconfig",
            RuntimeConfigPath,
            typeof(Program).Assembly.Location,
            mode
        };
        result.AddRange(arguments);
        return [.. result];
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
