using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.PlatformShared.Tests;

public sealed class OwnedProcessScopePlatformTests
{
    public static TheoryData<int> OwnershipStressIterations()
    {
        var data = new TheoryData<int>();
        var count = OperatingSystem.IsWindows() ? 10 : 100;
        for (var iteration = 1; iteration <= count; iteration++)
        {
            data.Add(iteration);
        }

        return data;
    }

    [Fact]
    public async Task WindowsJobContainsTreeButNotRunner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int? childPid = null;
        int? grandchildPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(OwnedProcessScopePlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);

            using var scope = await OwnedProcessScope.StartAsync(
                startInfo, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            var job = Assert.IsType<SafeFileHandle>(scope.WindowsJobHandle);
            Assert.True(IsInJob(scope.RootPid, job));
            Assert.True(IsInJob(childPid.Value, job));
            Assert.True(IsInJob(grandchildPid.Value, job));
            Assert.False(IsInJob(Environment.ProcessId, job));

            await scope.TerminateAsync(new CleanupDeadline(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            await scope.Host.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.False(IsAlive(scope.RootPid));
            Assert.False(IsAlive(childPid.Value));
            Assert.False(IsAlive(grandchildPid.Value));
        }
        finally
        {
            StopIfAlive(childPid);
            StopIfAlive(grandchildPid);
            await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(OwnershipStressIterations))]
    public async Task SnapshotFailureStillTerminatesRootChildAndGrandchild(int iteration)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var childMarker = Path.Combine(directory, "child.pid");
        var grandchildMarker = Path.Combine(directory, "grandchild.pid");
        int? childPid = null;
        int? grandchildPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(OwnedProcessScopePlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);

            using var cancellation = new CancellationTokenSource();
            var run = FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    $"scope.snapshot-failure.{iteration}", "root-child-grandchild", startInfo,
                    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), directory,
                    (_, _) => Task.FromException<FinalProcessSnapshot>(new IOException("snapshot unavailable"))),
                cancellation.Token);

            childPid = await ReadMarkerAsync(childMarker).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(grandchildMarker).ConfigureAwait(true);
            if (!OperatingSystem.IsWindows())
            {
                var rootGroup = GetProcessGroup(await ReadMarkerAsync(Path.Combine(directory, "root.pid")));
                Assert.True(rootGroup > 0);
                Assert.Equal(rootGroup, GetProcessGroup(childPid.Value));
                Assert.Equal(rootGroup, GetProcessGroup(grandchildPid.Value));
                Assert.NotEqual(GetProcessGroup(0), rootGroup);
            }
            await cancellation.CancelAsync().ConfigureAwait(true);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(130, result.ExitCode);
            Assert.False(IsAlive(result.RootPid), DescribeLinuxState(result.RootPid));
            Assert.False(IsAlive(childPid.Value), DescribeLinuxState(childPid.Value));
            Assert.False(IsAlive(grandchildPid.Value), DescribeLinuxState(grandchildPid.Value));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Contains(report.RootElement.GetProperty("Events").EnumerateArray(),
                item => item.GetProperty("Event").GetString() == "final_snapshot_failed");
        }
        finally
        {
            StopIfAlive(childPid);
            StopIfAlive(grandchildPid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExitedRootWithLivePipeHoldingDescendantStillReleasesScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "pipe-holder.pid");
        int? childPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(OwnedProcessScopePlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-exit-with-pipe-holder");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(marker);

            var clock = Stopwatch.StartNew();
            var run = FlightRecorderExecution.RunAsync(new ProcessExecutionRequest(
                "scope.root-exited", "pipe-holder", startInfo,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), directory),
                TestContext.Current.CancellationToken);
            childPid = await ReadMarkerAsync(marker).ConfigureAwait(true);
            Assert.True(IsAlive(childPid.Value));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            clock.Stop();

            Assert.Equal(2, result.ExitCode);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(IsAlive(result.RootPid));
            Assert.False(IsAlive(childPid.Value));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal("stream_drain_failed", report.RootElement.GetProperty("Outcome").GetString());
        }
        finally
        {
            StopIfAlive(childPid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int GetProcessGroup(int pid) => NativeMethods.GetProcessGroup(pid);

    private static string DescribeLinuxState(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return $"pid={pid}";
        }

        return $"pid={pid}, state={ReadLinuxStateCode(pid) ?? "unavailable"}";
    }

    private static string? ReadLinuxStateCode(int pid)
    {
        try
        {
            var stateLine = File.ReadLines($"/proc/{pid}/status")
                .FirstOrDefault(line => line.StartsWith("State:", StringComparison.Ordinal));
            var fields = stateLine?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return fields is { Length: > 1 } ? fields[1] : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsInJob(int pid, SafeFileHandle job)
    {
        using var process = Process.GetProcessById(pid);
        Assert.True(NativeMethods.IsProcessInJob(process.Handle, job, out var inJob));
        return inJob;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int GetProcessGroup(int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    }

    private static async Task<int> ReadMarkerAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            try
            {
                if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(
                        path, TestContext.Current.CancellationToken).ConfigureAwait(true),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // The fixture has created the marker but has not closed its write handle.
            }

            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException($"The fixture did not publish {Path.GetFileName(path)}.");
    }

    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var state = ReadLinuxStateCode(pid);
            if (state is "Z" or "X")
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

    private static void StopIfAlive(int? pid)
    {
        if (pid is not { } value || !IsAlive(value))
        {
            return;
        }

        using var process = Process.GetProcessById(value);
        process.Kill();
    }
}
