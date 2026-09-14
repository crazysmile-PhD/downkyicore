using System.Diagnostics;
using System.Globalization;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerCancellationComponentTests
{
    [Fact]
    public void BuildInvocationDisablesPersistentBuildServers()
    {
        var startInfo = BuildProcessRunner.CreateBuildStartInfo(
            "tests/Fixture.Tests/Fixture.Tests.csproj", "Release", noRestore: true);
        Assert.Contains("--disable-build-servers", startInfo.ArgumentList);
    }

    [Fact]
    public async Task BuildCancellationReapsLaunchContainedTree()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-build-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int[] pids = [];
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                "DownKyi.Architecture.Tests.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);

            using var cancellation = new CancellationTokenSource();
            var run = BuildProcessRunner.RunAsync(startInfo, cancellation.Token,
                TimeSpan.FromSeconds(5));
            pids =
            [
                await ReadMarkerAsync(Path.Combine(directory, "root.pid")).ConfigureAwait(true),
                await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true),
                await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true)
            ];
            Assert.All(pids, pid => Assert.True(IsExecutable(pid)));
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(8),
                    TestContext.Current.CancellationToken)).ConfigureAwait(true);
            Assert.All(pids, pid => Assert.False(IsExecutable(pid), $"pid={pid} remained executable"));
        }
        finally
        {
            foreach (var pid in pids)
            {
                if (IsExecutable(pid))
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
            }
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticSnapshotObservesButDoesNotOwnTheCurrentProcess()
    {
        var snapshot = ProcessTreeSnapshot.Capture(
            Environment.ProcessId, TimeSpan.FromSeconds(3));
        Assert.Contains(snapshot.Processes, process => process.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Completeness));
    }

    [Fact]
    public async Task PrimaryFailurePrecedesSecondaryCleanupFailure()
    {
        var primary = new TimeoutException("owned process still live");
        var secondary = new IOException("inspector teardown failed");
        var exception = await Record.ExceptionAsync(() => FailurePreservingTestCleanup.RunAsync(
            () => Task.FromException(primary),
            () => Task.FromException(secondary))).ConfigureAwait(true);
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(secondary, aggregate.InnerExceptions[1]);
    }

    [Fact]
    public async Task CancellationMapsTo130OnlyAfterCleanupSuccess()
    {
        var evidenceDirectory = Path.Combine(
            Path.GetTempPath(), $"downkyi-command-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        try
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(true);
            var success = await Program.RunCommandAsync([], (_, token) =>
                Task.FromCanceled<int>(token), cancellation.Token).ConfigureAwait(true);
            var failure = await Program.RunCommandAsync(
                ["run-project", "--evidence-directory", evidenceDirectory], (_, _) =>
                    Task.FromException<int>(new TimeoutException("cleanup failed")),
                cancellation.Token).ConfigureAwait(true);
            Assert.Equal(130, success);
            Assert.Equal(2, failure);
            Assert.Single(Directory.GetFiles(evidenceDirectory, "*.json"));
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    private static async Task<int> ReadMarkerAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path) && int.TryParse(
                        await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }
            catch (IOException) { }
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        throw new TimeoutException($"No marker: {path}");
    }

    private static bool IsExecutable(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var state = File.ReadLines($"/proc/{pid}/status")
                    .First(line => line.StartsWith("State:", StringComparison.Ordinal))
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[1];
                return state is not ("Z" or "X");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
