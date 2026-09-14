using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.PlatformShared.Tests;

public sealed class ProcessLifecycleOwnerPlatformTests
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
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);

            var scope = await ProcessLifecycleOwner.StartAsync(
                startInfo, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await using var scopeDisposal = scope.ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            var job = Assert.IsType<SafeFileHandle>(scope.WindowsJobHandle);
            Assert.True(IsInJob(scope.RootPid, job));
            Assert.True(IsInJob(childPid.Value, job));
            Assert.True(IsInJob(grandchildPid.Value, job));
            Assert.False(IsInJob(Environment.ProcessId, job));

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(true);
            var outcome = await scope.CompleteAsync(TimeSpan.FromSeconds(10), cancellation.Token)
                .ConfigureAwait(true);
            Assert.Equal(ProcessLifecycleTrigger.Cancelled, outcome.Trigger);
            Assert.True(outcome.CleanupSucceeded);
            Assert.True(outcome.HostExited);
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

    [Fact]
    public async Task LinuxLaunchContainsTreeBeforeWorkloadRuns()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-linux-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int? childPid = null;
        int? grandchildPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);
            var owner = await ProcessLifecycleOwner.StartAsync(
                startInfo, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await using var ownerDisposal = owner.ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            if (owner.UsesLinuxCgroup)
            {
                var membership = await File.ReadAllTextAsync(
                    $"/proc/{owner.Host.Id}/cgroup", TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Contains("/downkyi-", membership, StringComparison.Ordinal);
                Assert.Equal(membership, await File.ReadAllTextAsync(
                    $"/proc/{owner.RootPid}/cgroup", TestContext.Current.CancellationToken).ConfigureAwait(true));
                Assert.Equal(membership, await File.ReadAllTextAsync(
                    $"/proc/{childPid.Value}/cgroup", TestContext.Current.CancellationToken).ConfigureAwait(true));
                Assert.Equal(membership, await File.ReadAllTextAsync(
                    $"/proc/{grandchildPid.Value}/cgroup", TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
            else
            {
                Assert.Equal(owner.Host.Id, GetProcessGroup(owner.RootPid));
                Assert.Equal(owner.Host.Id, GetProcessGroup(childPid.Value));
                Assert.Equal(owner.Host.Id, GetProcessGroup(grandchildPid.Value));
            }
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(true);
            var outcome = await owner.CompleteAsync(TimeSpan.FromSeconds(10), cancellation.Token)
                .ConfigureAwait(true);
            Assert.Equal(ProcessLifecycleTrigger.Cancelled, outcome.Trigger);
            Assert.True(outcome.CleanupSucceeded);
            Assert.True(outcome.HostExited);
            AssertStopped(owner.RootPid);
            AssertStopped(childPid.Value);
            AssertStopped(grandchildPid.Value);
        }
        finally
        {
            StopIfAlive(grandchildPid);
            StopIfAlive(childPid);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LinuxFailedCgroupMembershipKeepsDirectoryOwnedForStartupRecovery()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // An ordinary directory cannot change /proc membership. It makes the
        // verification failure deterministic without requiring delegated cgroups.
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-cgroup-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup.kill"), "");
        File.WriteAllText(Path.Combine(directory, "cgroup.procs"), "");
        LinuxCgroupContainment? containment = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                LinuxCgroupContainment.TryAttachHostInDirectory(
                    Environment.ProcessId, directory, out containment));
            Assert.NotNull(containment);
            Assert.True(Directory.Exists(directory));

            File.Delete(Path.Combine(directory, "cgroup.kill"));
            File.Delete(Path.Combine(directory, "cgroup.procs"));
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            Assert.Throws<IOException>(() => containment.Remove());
            Directory.Delete(nested);
            containment.Remove();
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            containment?.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReusedPidInDiagnosticSnapshotCannotAuthorizeTermination()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-pid-reuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
        var unrelatedInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        unrelatedInfo.ArgumentList.Add("exec");
        unrelatedInfo.ArgumentList.Add("--runtimeconfig");
        unrelatedInfo.ArgumentList.Add(runtimeConfig);
        unrelatedInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
        unrelatedInfo.ArgumentList.Add("fixture-hold");
        using var unrelated = Process.Start(unrelatedInfo)
            ?? throw new InvalidOperationException("The unrelated fixture did not start.");
        int? rootPid = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-hold-marker");
            startInfo.ArgumentList.Add(Path.Combine(directory, "root.pid"));
            using var cancellation = new CancellationTokenSource();
            var run = FlightRecorderExecution.RunAsync(new ProcessExecutionRequest(
                "scope.pid-reuse", "unrelated-pid", startInfo,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), directory,
                (_, _) => new FinalProcessSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Completeness = FlightRecorder.SnapshotNotice,
                    Processes = [new ObservedProcess
                    {
                        Pid = unrelated.Id,
                        ParentPid = rootPid ?? 0,
                        StartTimeUtc = DateTimeOffset.UnixEpoch
                    }]
                }), cancellation.Token);
            rootPid = await ReadMarkerAsync(Path.Combine(directory, "root.pid")).ConfigureAwait(true);
            Assert.True(IsAlive(unrelated.Id));
            await cancellation.CancelAsync().ConfigureAwait(true);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(130, result.ExitCode);
            AssertStopped(rootPid.Value);
            Assert.True(IsAlive(unrelated.Id));
        }
        finally
        {
            if (!unrelated.HasExited)
            {
                unrelated.Kill();
            }
            await unrelated.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            StopIfAlive(rootPid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
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
        DateTimeOffset? childStartTimeUtc = null;
        DateTimeOffset? grandchildStartTimeUtc = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
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
                    (_, _) => throw new IOException("snapshot unavailable")),
                cancellation.Token);

            childPid = await ReadMarkerAsync(childMarker).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(grandchildMarker).ConfigureAwait(true);
            if (!OperatingSystem.IsWindows())
            {
                childStartTimeUtc = ReadStartTimeUtc(childPid.Value);
                grandchildStartTimeUtc = ReadStartTimeUtc(grandchildPid.Value);
            }
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
            AssertStopped(result.RootPid, result.RootStartTimeUtc);
            AssertStopped(childPid.Value, childStartTimeUtc);
            AssertStopped(grandchildPid.Value, grandchildStartTimeUtc);
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
    public async Task MacStoppedAssertionRejectsPersistentChild()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-leak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? root = null;
        int? childPid = null;
        int? grandchildPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);
            root = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The persistent-tree fixture did not start.");
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            var childStartTimeUtc = ReadStartTimeUtc(childPid.Value);

            var failure = Record.Exception(() => AssertStopped(childPid.Value, childStartTimeUtc));
            Assert.NotNull(failure);
            Assert.Contains($"pid={childPid.Value}, ps status=", failure.Message, StringComparison.Ordinal);
            Assert.True(IsAlive(childPid.Value));
            Assert.True(IsAlive(grandchildPid.Value));
        }
        finally
        {
            if (root is { HasExited: false })
            {
                root.Kill(entireProcessTree: true);
            }
            if (root is not null)
            {
                await root.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            StopIfAlive(grandchildPid);
            StopIfAlive(childPid);
            root?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MacStoppedAssertionRejectsFirstLiveObservation()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        startInfo.ArgumentList.Add("60");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The teardown fixture did not start.");
        try
        {
            var expectedStartTimeUtc = process.StartTime.ToUniversalTime();
            var observations = 0;
            var failure = Record.Exception(() => AssertStopped(process.Id, expectedStartTimeUtc, pid =>
            {
                var state = ReadMacState(pid);
                if (++observations == 1 && IsLiveMacState(state))
                {
                    process.Kill();
                    Assert.True(process.WaitForExit(5000));
                }
                return state;
            }));
            Assert.NotNull(failure);
            Assert.Contains($"pid={process.Id}, ps status=", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, observations);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public void MacZombieStateIsStopped()
    {
        Assert.False(IsLiveMacState(null));
        Assert.False(IsLiveMacState("Z"));
        Assert.False(IsLiveMacState("Z+"));
        Assert.False(IsLiveMacState("X"));
        Assert.True(IsLiveMacState("R+"));
        Assert.True(IsLiveMacState("S"));
    }

    [Fact]
    public async Task UnixGroupWaitRejectsPersistentGrandchildAfterRootAndParentExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-leak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ProcessLifecycleOwner? scope = null;
        int? childPid = null;
        int? grandchildPid = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
            startInfo.ArgumentList.Add("fixture-tree-root");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(directory);
            scope = await ProcessLifecycleOwner.StartAsync(startInfo, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);

            StopIfAlive(scope.RootPid);
            StopIfAlive(childPid);
            Assert.True(IsAlive(grandchildPid.Value));

            var failure = await Record.ExceptionAsync(() => scope.WaitForGroupStoppedAsync(
                new CleanupDeadline(TimeSpan.FromSeconds(1)),
                groupId => UnixProcessGroupInspector.ReadLiveMember(groupId, scope.Host.Id)))
                .ConfigureAwait(true);
            var timeout = Assert.IsType<TimeoutException>(failure);
            Assert.Contains($"pid={grandchildPid.Value}", timeout.Message, StringComparison.Ordinal);
            Assert.True(IsAlive(grandchildPid.Value));

            var outcome = await scope.CompleteAsync(TimeSpan.Zero, CancellationToken.None)
                .ConfigureAwait(true);
            Assert.True(outcome.HostExited);
            AssertStopped(grandchildPid.Value);
        }
        finally
        {
            try
            {
                if (scope is not null)
                {
                    await scope.DisposeAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                StopIfAlive(grandchildPid);
                StopIfAlive(childPid);
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnixInspectorFailureAfterLiveObservationStillReapsOwnerAndPreservesEvidence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
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
        startInfo.ArgumentList.Add("fixture-hold");
        var owner = await ProcessLifecycleOwner.StartAsync(
            startInfo, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        await using var ownerDisposal = owner.ConfigureAwait(true);
        if (owner.UsesLinuxCgroup)
        {
            return; // This seam observes process groups; the cgroup backend is covered by containment tests.
        }

        owner.StartOutputCapture(
            new TailBuffer(1), new TailBuffer(1), new SensitiveEvidenceRedactor(""));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);
        var observations = 0;
        var outcome = await owner.CompleteAsync(
            TimeSpan.FromSeconds(10), cancellation.Token,
            unixGroupObserver: _ => ++observations == 1
                ? "pid=5678, state=S"
                : throw new OperationCanceledException("inspector reap failed"))
            .ConfigureAwait(true);

        Assert.Equal(2, observations);
        Assert.Equal(ProcessLifecycleTrigger.Cancelled, outcome.Trigger);
        Assert.False(outcome.CleanupSucceeded);
        var primary = Assert.IsType<TimeoutException>(outcome.PrimaryFailure);
        Assert.Contains("pid=5678, state=S", primary.Message,
            StringComparison.Ordinal);
        Assert.Equal("pid=5678, state=S", outcome.LiveEvidence);
        Assert.IsType<OperationCanceledException>(outcome.SecondaryCleanupFailure);
        Assert.True(outcome.HostExited);
        Assert.True(owner.OutputCaptureTasksTerminal);
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
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
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

    private static void AssertStopped(
        int pid,
        DateTimeOffset? expectedStartTimeUtc = null,
        Func<int, string?>? readMacState = null)
    {
        string? state = null;
        if (OperatingSystem.IsMacOS())
        {
            state = (readMacState ?? ReadMacState)(pid);
            if (!IsLiveMacState(state))
            {
                return;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            state = ReadLinuxStateCode(pid);
            if (state is null or "Z" or "X")
            {
                return;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.Fail($"pid={pid}, ps status={state}, original start={expectedStartTimeUtc:O}");
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Fail($"pid={pid}, state={state}, original start={expectedStartTimeUtc:O}");
        }
        else if (IsAlive(pid))
        {
            Assert.Fail(DescribeProcessState(pid));
        }
    }

    private static bool IsLiveMacState(string? state) =>
        state is not null && state[0] is not ('Z' or 'X');

    private static string DescribeProcessState(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return $"pid={pid}, state={ReadLinuxStateCode(pid) ?? "unavailable"}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"pid={pid}, ps status={ReadMacState(pid) ?? "gone"}";
        }

        return $"pid={pid}";
    }

    [Fact]
    public async Task BuildCancellationUsesLaunchContainmentForRootChildAndGrandchild()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-build-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int? rootPid = null;
        int? childPid = null;
        int? grandchildPid = null;
        DateTimeOffset? rootStartTimeUtc = null;
        DateTimeOffset? childStartTimeUtc = null;
        DateTimeOffset? grandchildStartTimeUtc = null;
        try
        {
            var runtimeConfig = Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(typeof(ProcessLifecycleOwnerPlatformTests).Assembly.Location)}.runtimeconfig.json");
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
            rootPid = await ReadMarkerAsync(Path.Combine(directory, "root.pid")).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            if (!OperatingSystem.IsWindows())
            {
                rootStartTimeUtc = ReadStartTimeUtc(rootPid.Value);
                childStartTimeUtc = ReadStartTimeUtc(childPid.Value);
                grandchildStartTimeUtc = ReadStartTimeUtc(grandchildPid.Value);
            }

            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(8),
                    TestContext.Current.CancellationToken)).ConfigureAwait(true);
            AssertStopped(rootPid.Value, rootStartTimeUtc);
            AssertStopped(childPid.Value, childStartTimeUtc);
            AssertStopped(grandchildPid.Value, grandchildStartTimeUtc);
        }
        finally
        {
            StopIfAlive(grandchildPid);
            StopIfAlive(childPid);
            StopIfAlive(rootPid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            Directory.Delete(directory, recursive: true);
        }
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
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException exception) when (exception.HResult == 3) // ESRCH: process vanished during procfs enumeration.
        {
            return null;
        }
    }

    private static string? ReadMacState(int pid)
    {
        using var ps = new Process
        {
            StartInfo = new ProcessStartInfo("ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        ps.StartInfo.ArgumentList.Add("-p");
        ps.StartInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        ps.StartInfo.ArgumentList.Add("-o");
        ps.StartInfo.ArgumentList.Add("stat=");
        ps.Start();
        if (!ps.WaitForExit(2000))
        {
            ps.Kill();
            throw new TimeoutException($"ps did not return the state for pid {pid}.");
        }

        var state = ps.StandardOutput.ReadToEnd().Trim();
        var error = ps.StandardError.ReadToEnd().Trim();
        if (ps.ExitCode is not (0 or 1) || error.Length > 0)
        {
            throw new InvalidOperationException($"ps could not inspect pid {pid}: {error}");
        }

        return state.Length == 0 ? null : state;
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

    private static DateTimeOffset ReadStartTimeUtc(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime();
    }

    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            var state = ReadMacState(pid);
            return IsLiveMacState(state);
        }

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
