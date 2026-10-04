using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;
using DownKyi.TestInfrastructure;
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
                startInfo, TestContext.Current.CancellationToken).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);
            var job = Assert.IsType<SafeFileHandle>(scope.WindowsJobHandle);
            Assert.True(IsInJob(scope.RootPid, job));
            Assert.True(IsInJob(childPid.Value, job));
            Assert.True(IsInJob(grandchildPid.Value, job));
            Assert.False(IsInJob(Environment.ProcessId, job));

            await scope.TerminateAsync(new CleanupDeadline(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            Assert.False(IsAlive(scope.RootPid));
            Assert.False(IsAlive(childPid.Value));
            Assert.False(IsAlive(grandchildPid.Value));
            await scope.Host.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
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
    public async Task WindowsLargeLaunchPayloadCompletesScopeStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int launchPayloadLength = 8 * 1024 * 1024;
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-large-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var fixture = new Process
        {
            StartInfo = CreateLargeLaunchFixtureStartInfo(directory, launchPayloadLength)
        };
        try
        {
            Assert.True(fixture.Start());
            await fixture.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var standardOutput = await fixture.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var standardError = await fixture.StandardError.ReadToEndAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(
                fixture.ExitCode == 0,
                $"Large-launch fixture exited with {fixture.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{standardError}");

            var evidencePath = Assert.Single(Directory.GetFiles(directory, "*.json"));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                evidencePath,
                TestContext.Current.CancellationToken).ConfigureAwait(true));
            var events = report.RootElement.GetProperty("Events").EnumerateArray().ToArray();
            var startupPhases = events
                .Where(item => item.GetProperty("Event").GetString() == "scope_launch_phase")
                .Select(item => item.GetProperty("Detail").GetString() ?? string.Empty)
                .ToArray();
            Assert.Equal(
                [
                    "host_started",
                    "launch_payload_write_begin",
                    "launch_payload_write_completed",
                    "control_connection_wait_begin",
                    "control_connected",
                    "handshake_read_begin",
                    "handshake_received"
                ],
                startupPhases);
            Assert.Contains(
                events,
                item => item.GetProperty("Event").GetString() == "process_start");
        }
        finally
        {
            if (fixture is { HasExited: false })
            {
                fixture.Kill(entireProcessTree: true);
                await fixture.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(OwnershipStressIterations))]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The test must retain the run failure while fallback cleanup continues.")]
    public async Task SnapshotFailureStillTerminatesRootChildAndGrandchild(int iteration)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var childMarker = Path.Combine(directory, "child.pid");
        var grandchildMarker = Path.Combine(directory, "grandchild.pid");
        int? rootPid = null;
        int? childPid = null;
        int? grandchildPid = null;
        using var cancellation = new CancellationTokenSource();
        var startupReady = new TaskCompletionSource<ProcessExecutionStartup>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProcessExecutionResult>? run = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
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

                run = FlightRecorderExecution.RunAsync(
                    new ProcessExecutionRequest(
                        $"scope.snapshot-failure.{iteration}", "root-child-grandchild", startInfo,
                        TimeSpan.FromSeconds(5), directory,
                        (_, _) => Task.FromException<FinalProcessSnapshot>(new IOException("snapshot unavailable")),
                        StartupReady: startup => startupReady.TrySetResult(startup)),
                    cancellation.Token);

                var firstCompletion = await Task.WhenAny(startupReady.Task, run).ConfigureAwait(true);
                Assert.Same(startupReady.Task, firstCompletion);
                var startup = await startupReady.Task.ConfigureAwait(true);
                rootPid = startup.RootPid;
                childPid = await ReadMarkerAsync(childMarker).ConfigureAwait(true);
                grandchildPid = await ReadMarkerAsync(grandchildMarker).ConfigureAwait(true);
                if (!OperatingSystem.IsWindows())
                {
                    var rootGroup = GetProcessGroup(rootPid.Value);
                    Assert.True(rootGroup > 0);
                    Assert.Equal(rootGroup, GetProcessGroup(childPid.Value));
                    Assert.Equal(rootGroup, GetProcessGroup(grandchildPid.Value));
                    Assert.NotEqual(GetProcessGroup(0), rootGroup);
                }

                await cancellation.CancelAsync().ConfigureAwait(true);
                var result = await run.ConfigureAwait(true);
                Assert.Equal(130, result.ExitCode);
                AssertStopped(result.RootPid);
                AssertStopped(childPid.Value);
                AssertStopped(grandchildPid.Value);
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                    result.EvidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
                var events = report.RootElement.GetProperty("Events")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("Event").GetString())
                    .ToArray();
                var processStartIndex = Array.IndexOf(events, "process_start");
                var snapshotFailureIndex = Array.IndexOf(events, "final_snapshot_failed");
                Assert.InRange(processStartIndex, 0, snapshotFailureIndex - 1);
            },
            async () =>
            {
                await FailurePreservingTestCleanup.RunAsync(
                    async () =>
                    {
                        if (run is not null)
                        {
                            await FailurePreservingTestCleanup.CancelJoinValidateAndCleanupAsync(
                                run,
                                async () => await cancellation.CancelAsync().ConfigureAwait(false),
                                terminalResult => Assert.Equal(130, terminalResult.ExitCode),
                                () => StopIfAlive(rootPid),
                                () => StopIfAlive(childPid),
                                () => StopIfAlive(grandchildPid)).ConfigureAwait(true);
                        }
                    },
                    async () =>
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                                directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                        }

                        Directory.Delete(directory, recursive: true);
                    }).ConfigureAwait(true);
            }).ConfigureAwait(true);
    }

    [Fact]
    public async Task FixtureCleanupJoinsRunAfterCancellationAndFallbackStopFailuresBeforeDeletingResources()
    {
        var runCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallbackAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalCompletion = CompleteRunAfterFallbackAsync();
        var cancellationRequested = false;
        var cleanupAttempts = 0;
        var resultValidated = false;
        var resourcesDeleted = false;
        var runWasTerminalAtDeletion = false;
        var observedFailure = await Record.ExceptionAsync(
            async () =>
            {
                try
                {
                    await FailurePreservingTestCleanup.CancelJoinValidateAndCleanupAsync(
                        runCompletion.Task,
                        () =>
                        {
                            cancellationRequested = true;
                            return Task.FromException(
                                new InvalidOperationException("Simulated cancellation failure."));
                        },
                        result =>
                        {
                            Assert.Equal(130, result);
                            resultValidated = true;
                        },
                        () =>
                        {
                            cleanupAttempts++;
                            if (cleanupAttempts == 1)
                            {
                                fallbackAttempted.TrySetResult();
                                throw new InvalidOperationException("Simulated fallback stop failure.");
                            }
                        }).ConfigureAwait(true);
                }
                finally
                {
                    runWasTerminalAtDeletion = runCompletion.Task.IsCompleted;
                    resourcesDeleted = true;
                }
            }).ConfigureAwait(true);

        var aggregate = Assert.IsType<AggregateException>(observedFailure);
        Assert.Contains(
            aggregate.InnerExceptions,
            failure => failure is InvalidOperationException &&
                       failure.Message.Contains("cancellation failure", StringComparison.Ordinal));
        Assert.Contains(
            aggregate.InnerExceptions,
            failure => failure is InvalidOperationException &&
                       failure.Message.Contains("fallback stop failure", StringComparison.Ordinal));
        Assert.True(cancellationRequested);
        Assert.True(resultValidated);
        Assert.Equal(2, cleanupAttempts);
        await terminalCompletion.ConfigureAwait(true);
        Assert.True(terminalCompletion.IsCompletedSuccessfully);
        Assert.True(runCompletion.Task.IsCompletedSuccessfully);
        Assert.True(resourcesDeleted);
        Assert.True(runWasTerminalAtDeletion);

        async Task CompleteRunAfterFallbackAsync()
        {
            await fallbackAttempted.Task.ConfigureAwait(false);
            runCompletion.TrySetResult(130);
        }
    }

    [Fact]
    public async Task ExitedRootWithLivePipeHoldingDescendantStillReleasesScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "pipe-holder.pid");
        var releaseName = OperatingSystem.IsWindows()
            ? $"Local\\downkyi-scope-pipe-holder-release-{Guid.NewGuid():N}"
            : null;
        using var release = releaseName is null
            ? null
            : new EventWaitHandle(initialState: false, EventResetMode.ManualReset, releaseName);
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
            if (releaseName is not null)
            {
                startInfo.ArgumentList.Add(releaseName);
            }

            var run = FlightRecorderExecution.RunAsync(new ProcessExecutionRequest(
                "scope.root-exited", "pipe-holder", startInfo,
                TimeSpan.FromSeconds(2), directory),
                TestContext.Current.CancellationToken);
            childPid = await ReadMarkerAsync(marker).ConfigureAwait(true);
            Assert.True(IsAlive(childPid.Value));
            release?.Set();
            var result = await run.ConfigureAwait(true);
            Assert.Equal(2, result.ExitCode);
            Assert.False(IsAlive(result.RootPid));
            Assert.False(IsAlive(childPid.Value));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal("stream_drain_failed", report.RootElement.GetProperty("Outcome").GetString());
        }
        finally
        {
            release?.Set();
            StopIfAlive(childPid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    directory, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxGroupProbeDoesNotHideLiveGrandchild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        OwnedProcessScope? scope = null;
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
            scope = await OwnedProcessScope.StartAsync(
                startInfo, TestContext.Current.CancellationToken).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);

            StopIfAlive(scope.RootPid);
            StopIfAlive(childPid);
            await scope.Host.WaitForExitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(IsAlive(grandchildPid.Value));

            var failure = await Record.ExceptionAsync(() => scope.WaitForLinuxProcessGroupToEmptyAsync(
                new CleanupDeadline(TimeSpan.FromMilliseconds(100)))).ConfigureAwait(true);
            Assert.IsType<TimeoutException>(failure);

            await scope.TerminateAsync(new CleanupDeadline(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            Assert.False(IsAlive(grandchildPid.Value));
        }
        finally
        {
            scope?.Dispose();
            StopIfAlive(childPid);
            StopIfAlive(grandchildPid);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MacGroupProbeDoesNotHideLiveGrandchild()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-scope-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        OwnedProcessScope? scope = null;
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
            scope = await OwnedProcessScope.StartAsync(
                startInfo, TestContext.Current.CancellationToken).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);

            StopIfAlive(scope.RootPid);
            StopIfAlive(childPid);
            await scope.Host.WaitForExitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(IsAlive(grandchildPid.Value));

            var failure = await Record.ExceptionAsync(() => scope.WaitForMacProcessGroupToEmptyAsync(
                new CleanupDeadline(TimeSpan.FromMilliseconds(100)))).ConfigureAwait(true);
            Assert.IsType<TimeoutException>(failure);

            await scope.TerminateAsync(new CleanupDeadline(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            Assert.False(IsAlive(grandchildPid.Value));
        }
        finally
        {
            scope?.Dispose();
            StopIfAlive(childPid);
            StopIfAlive(grandchildPid);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int GetProcessGroup(int pid) => NativeMethods.GetProcessGroup(pid);

    private static ProcessStartInfo CreateLargeLaunchFixtureStartInfo(
        string evidenceDirectory,
        int payloadLength)
    {
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(typeof(OwnedProcessScopePlatformTests).Assembly.Location)}.runtimeconfig.json");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
        startInfo.ArgumentList.Add("fixture-large-launch");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(evidenceDirectory);
        startInfo.ArgumentList.Add(payloadLength.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static void AssertStopped(int pid)
    {
        if (IsAlive(pid))
        {
            Assert.Fail(DescribeProcessState(pid));
        }
    }

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
    public async Task BuildSnapshotFailureStillTerminatesDescendants()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-build-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        CancellationTokenSource? cancellation = null;
        Task<int>? build = null;
        int? rootPid = null;
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
            var snapshotFailure = new IOException("snapshot unavailable");
            cancellation = new CancellationTokenSource();
            build = BuildProcessRunner.RunAsync(
                startInfo,
                cancellation.Token,
                TimeSpan.FromSeconds(5),
                captureSnapshotAsync: (_, _) => Task.FromException<FinalProcessSnapshot>(snapshotFailure));
            rootPid = await ReadMarkerAsync(Path.Combine(directory, "root.pid")).ConfigureAwait(true);
            childPid = await ReadMarkerAsync(Path.Combine(directory, "child.pid")).ConfigureAwait(true);
            grandchildPid = await ReadMarkerAsync(Path.Combine(directory, "grandchild.pid")).ConfigureAwait(true);

            if (OperatingSystem.IsLinux())
            {
                var ownerPid = ReadLinuxParentPid(rootPid.Value);
                Assert.True(ownerPid > 0);
                Assert.Equal(ownerPid, GetProcessGroup(rootPid.Value));
                Assert.Equal(rootPid, ReadLinuxParentPid(childPid.Value));
                Assert.Equal(childPid, ReadLinuxParentPid(grandchildPid.Value));
            }

            await cancellation.CancelAsync().ConfigureAwait(true);
            var failure = await Record.ExceptionAsync(() => build).ConfigureAwait(true);

            Assert.Same(snapshotFailure, failure);
            AssertStopped(rootPid.Value);
            AssertStopped(childPid.Value);
            AssertStopped(grandchildPid.Value);
            if (OperatingSystem.IsLinux())
            {
                Assert.Null(ReadLinuxStateCode(rootPid.Value));
                Assert.Null(ReadLinuxStateCode(childPid.Value));
                Assert.Null(ReadLinuxStateCode(grandchildPid.Value));
            }
        }
        finally
        {
            if (build is { IsCompleted: false } && cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
            }

            if (build is not null)
            {
                _ = await Record.ExceptionAsync(() => build).ConfigureAwait(true);
            }

            cancellation?.Dispose();
            StopIfAlive(rootPid);
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

    private static int? ReadLinuxParentPid(int pid)
    {
        try
        {
            var parentLine = File.ReadLines($"/proc/{pid}/status")
                .FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
            var fields = parentLine?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return fields is { Length: > 1 } &&
                   int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid)
                ? parentPid
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        ps.WaitForExit();

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
        while (true)
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
    }

    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            var state = ReadMacState(pid);
            return state is not null && state[0] is not ('Z' or 'X');
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
