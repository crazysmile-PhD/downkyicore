using System.Diagnostics;
using DownKyi.TestInfrastructure;

namespace DownKyi.Architecture.Tests;

[Collection("External process lifecycle")]
public sealed class ExternalProcessTestHarnessTests
{
    [Fact]
    public void RunDrainsBothStreamsBeforeReturningNonzeroExit()
    {
        const int outputLength = 131_072;
        var startInfo = CreatePowerShellStartInfo(
            $"[Console]::Out.Write(('o' * {outputLength})); " +
            $"[Console]::Error.Write(('e' * {outputLength})); exit 17");

        var result = ExternalProcessTestHarness.Run(
            startInfo,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5));

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(outputLength, result.StandardOutput.Length);
        Assert.Equal(outputLength, result.StandardError.Length);
    }

    [Fact]
    public async Task RunAsyncDrainsBothStreamsBeforeReturningNonzeroExit()
    {
        const int outputLength = 131_072;
        var startInfo = CreatePowerShellStartInfo(
            $"[Console]::Out.Write(('o' * {outputLength})); " +
            $"[Console]::Error.Write(('e' * {outputLength})); exit 17");

        var result = await ExternalProcessTestHarness.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(outputLength, result.StandardOutput.Length);
        Assert.Equal(outputLength, result.StandardError.Length);
    }

    [Fact]
    public async Task RunAsyncTimesOutAfterTreeCleanupReapAndDrain()
    {
        var childCommand = Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes(
                "[Threading.ManualResetEventSlim]::new($false).Wait()"));
        var startInfo = CreatePowerShellStartInfo(
            "$child = Start-Process -FilePath (Get-Process -Id $PID).Path " +
            $"-ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','{childCommand}') " +
            "-PassThru -NoNewWindow; " +
            "[Console]::Out.WriteLine(('child-pid=' + $child.Id)); " +
            "[Console]::Error.WriteLine('stderr-before-timeout'); " +
            "[Console]::Out.Flush(); [Console]::Error.Flush(); " +
            "[Threading.ManualResetEventSlim]::new($false).Wait()");

        var exception = await Assert.ThrowsAsync<ExternalProcessTimeoutException>(
            () => ExternalProcessTestHarness.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Empty(exception.CleanupFailures);
        Assert.Contains("child-pid=", exception.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("stderr-before-timeout", exception.StandardError, StringComparison.Ordinal);
        Assert.False(IsProcessAlive(exception.ProcessId));

        var childLine = exception.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("child-pid=", StringComparison.Ordinal));
        var childProcessId = int.Parse(
            childLine["child-pid=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(IsProcessAlive(childProcessId));
    }

    [Fact]
    public async Task RunAsyncKillsInheritedPipeDescendantAfterRootExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-inherited-pipe-{Guid.NewGuid():N}");
        var childPidPath = Path.Combine(fixtureRoot, "child.pid");
        Directory.CreateDirectory(fixtureRoot);

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var startInfo = new ProcessStartInfo("/bin/sh")
                {
                    WorkingDirectory = fixtureRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("sleep 60 & echo $! > \"$1\"; exit 0");
                startInfo.ArgumentList.Add("inherited-pipe-fixture");
                startInfo.ArgumentList.Add(childPidPath);

                await Assert.ThrowsAsync<TimeoutException>(
                    () => ExternalProcessTestHarness.RunAsync(
                        startInfo,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(2),
                        TestContext.Current.CancellationToken)).ConfigureAwait(true);

                var childProcessId = int.Parse(
                    await File.ReadAllTextAsync(
                        childPidPath,
                        TestContext.Current.CancellationToken).ConfigureAwait(true),
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.False(IsProcessAlive(childProcessId));
            },
            () =>
            {
                Directory.Delete(fixtureRoot, recursive: true);
                return Task.CompletedTask;
            }).ConfigureAwait(true);
    }

    [Fact]
    public async Task RunAsyncKeepsCallerCancellationDistinctFromTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var startInfo = CreatePowerShellStartInfo(
            "[Threading.ManualResetEventSlim]::new($false).Wait()");

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ExternalProcessTestHarness.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(5),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunWithCleanupAsyncPreservesBodyFailureBeforeCleanupFailure()
    {
        var laterCleanupRan = false;
        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => ExternalProcessTestHarness.RunWithCleanupAsync(
                () => Task.FromException(new InvalidOperationException("primary failure")),
                () => Task.FromException(new IOException("cleanup failure")),
                () =>
                {
                    laterCleanupRan = true;
                    return Task.CompletedTask;
                }));

        Assert.True(laterCleanupRan);
        Assert.Collection(
            exception.InnerExceptions,
            primary => Assert.Equal("primary failure", primary.Message),
            cleanup => Assert.Equal("cleanup failure", cleanup.Message));
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string command)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static bool IsProcessAlive(int processId)
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
