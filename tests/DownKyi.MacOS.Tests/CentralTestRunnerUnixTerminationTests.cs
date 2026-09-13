using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using DownKyi.CentralTestRunner;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class CentralTestRunnerUnixTerminationTests
{
    public static TheoryData<int> Iterations()
    {
        var data = new TheoryData<int>();
        for (var iteration = 1; iteration <= 100; iteration++)
        {
            data.Add(iteration);
        }

        return data;
    }

    public static TheoryData<int, string> LegacyKillScenarios()
    {
        var data = new TheoryData<int, string>();
        for (var iteration = 1; iteration <= 100; iteration++)
        {
            data.Add(iteration, "steady");
            data.Add(iteration, "exiting-grandchild");
            data.Add(iteration, "exiting-child");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task UnixTerminationKillsCapturedDescendants(int iteration)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-unix-tree-{iteration}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "tree.sh");
        await File.WriteAllTextAsync(script, """
            #!/bin/sh
            /bin/sh -c '/bin/sleep 60 & echo $! > "$1/grandchild.pid"; echo ready; wait' child "$1" &
            echo $! > "$1/child.pid"
            wait
            """, TestContext.Current.CancellationToken);
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(directory);
        using var root = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The process-tree fixture did not start.");
        try
        {
            Assert.Equal("ready", await root.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var childPid = int.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "child.pid"), TestContext.Current.CancellationToken),
                CultureInfo.InvariantCulture);
            var grandchildPid = int.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "grandchild.pid"), TestContext.Current.CancellationToken),
                CultureInfo.InvariantCulture);
            var deadline = new CleanupDeadline(TimeSpan.FromSeconds(5));
            var snapshot = await ProcessTreeSnapshot.CaptureAsync(root.Id, deadline.SnapshotWindow);
            Assert.Contains(snapshot.Processes, process => process.Pid == childPid);
            Assert.Contains(snapshot.Processes, process => process.Pid == grandchildPid);

            Assert.Empty(await OwnedProcessTerminator.TerminateAsync(root, snapshot.Processes, deadline));
            await root.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(deadline.Remaining, TestContext.Current.CancellationToken);
            await BuildProcessRunner.WaitForOwnedProcessesToExitAsync(snapshot.Processes, deadline.Remaining);
            Assert.All(snapshot.Processes, observed =>
                Assert.Equal(ProcessIdentityState.Gone, ObservedProcessIdentity.Observe(observed)));
        }
        finally
        {
            foreach (var marker in new[] { "grandchild.pid", "child.pid" })
            {
                var markerPath = Path.Combine(directory, marker);
                if (File.Exists(markerPath) &&
                    int.TryParse(await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken),
                        CultureInfo.InvariantCulture, out var pid))
                {
                    try
                    {
                        using var descendant = Process.GetProcessById(pid);
                        if (!descendant.HasExited)
                        {
                            descendant.Kill();
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A fixture process may have exited after the assertion.
                    }
                }
            }

            if (!root.HasExited)
            {
                root.Kill();
            }

            await root.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminationSignalsSameIdentityButNeverSignalsReusedPid()
    {
        using var root = StartSleep();
        using var reusedPid = StartSleep();
        var signals = new List<int>();
        try
        {
            var snapshot = new[]
            {
                new ObservedProcess
                {
                    Pid = reusedPid.Id,
                    ParentPid = root.Id,
                    StartTimeUtc = reusedPid.StartTime.ToUniversalTime().AddSeconds(-1)
                },
                new ObservedProcess
                {
                    Pid = root.Id,
                    ParentPid = Environment.ProcessId,
                    StartTimeUtc = root.StartTime.ToUniversalTime()
                }
            };
            Assert.Equal(ProcessIdentityState.Same, ObservedProcessIdentity.Observe(snapshot[1]));
            Assert.Equal(ProcessIdentityState.Gone, ObservedProcessIdentity.Observe(snapshot[0]));

            var failures = await OwnedProcessTerminator.TerminateAsync(
                root, snapshot, new CleanupDeadline(TimeSpan.FromSeconds(1)),
                signalProcess: pid =>
                {
                    signals.Add(pid);
                    if (pid == root.Id)
                    {
                        root.Kill();
                    }
                });

            Assert.Empty(failures);
            Assert.Contains(root.Id, signals);
            Assert.DoesNotContain(reusedPid.Id, signals);
            Assert.False(reusedPid.HasExited);
        }
        finally
        {
            await StopAsync(root);
            await StopAsync(reusedPid);
        }
    }

    [Fact]
    public async Task TerminationTreatsExitedOriginalAsGone()
    {
        using var root = StartSleep();
        using var exited = StartSleep();
        var observed = new ObservedProcess
        {
            Pid = exited.Id,
            ParentPid = root.Id,
            StartTimeUtc = exited.StartTime.ToUniversalTime()
        };
        var signals = new List<int>();
        try
        {
            exited.Kill();
            await exited.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(ProcessIdentityState.Gone, ObservedProcessIdentity.Observe(observed));

            var failures = await OwnedProcessTerminator.TerminateAsync(
                root, [observed], new CleanupDeadline(TimeSpan.FromSeconds(1)),
                signalProcess: pid =>
                {
                    signals.Add(pid);
                    if (pid == root.Id)
                    {
                        root.Kill();
                    }
                });

            Assert.Empty(failures);
            Assert.DoesNotContain(exited.Id, signals);
        }
        finally
        {
            await StopAsync(root);
            await StopAsync(exited);
        }
    }

    [Fact]
    public async Task TerminationRechecksUnknownAndSignalsOnlyAfterIdentityBecomesSame()
    {
        using var root = StartSleep();
        var descendant = new ObservedProcess
        {
            Pid = root.Id + 1000000,
            ParentPid = root.Id,
            StartTimeUtc = DateTimeOffset.UtcNow
        };
        var observations = 0;
        var signals = new List<int>();
        try
        {
            var failures = await OwnedProcessTerminator.TerminateAsync(
                root, [descendant], new CleanupDeadline(TimeSpan.FromSeconds(1)),
                _ => ++observations == 1 ? ProcessIdentityState.Unknown : ProcessIdentityState.Same,
                pid =>
                {
                    signals.Add(pid);
                    if (pid == root.Id)
                    {
                        root.Kill();
                    }
                });

            Assert.Empty(failures);
            Assert.True(observations >= 2);
            Assert.Contains(descendant.Pid, signals);
            Assert.True(signals.IndexOf(root.Id) < signals.IndexOf(descendant.Pid));
        }
        finally
        {
            await StopAsync(root);
        }
    }

    [Fact]
    public async Task UnknownIdentityFailsClosedWithinCleanupDeadline()
    {
        using var root = StartSleep();
        var descendant = new ObservedProcess
        {
            Pid = root.Id + 1000000,
            ParentPid = root.Id,
            StartTimeUtc = DateTimeOffset.UtcNow
        };
        var signals = new List<int>();
        var clock = Stopwatch.StartNew();
        try
        {
            var failures = await OwnedProcessTerminator.TerminateAsync(
                root, [descendant], new CleanupDeadline(TimeSpan.FromMilliseconds(100)),
                _ => ProcessIdentityState.Unknown,
                pid =>
                {
                    signals.Add(pid);
                    if (pid == root.Id)
                    {
                        root.Kill();
                    }
                });

            Assert.Single(failures);
            Assert.Contains("identity remained unknown", failures[0], StringComparison.Ordinal);
            Assert.DoesNotContain(descendant.Pid, signals);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            await StopAsync(root);
        }
    }

    private static Process StartSleep()
    {
        var startInfo = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        startInfo.ArgumentList.Add("60");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The sleep fixture did not start.");
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Theory]
    [MemberData(nameof(LegacyKillScenarios))]
    public async Task LegacySynchronousTreeKillReturnsWithinBound(int iteration, string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-legacy-tree-{scenario}-{iteration}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "tree.sh");
        var scriptContents = scenario switch
        {
            "steady" => """
            #!/bin/sh
            echo $$ > "$1/root.pid"
            /bin/sh -c '/bin/sleep 60 & echo $! > "$1/grandchild.pid"; echo ready; wait' child "$1" &
            echo $! > "$1/child.pid"
            wait
            """,
            "exiting-grandchild" => """
            #!/bin/sh
            echo $$ > "$1/root.pid"
            /bin/sh -c '/bin/sleep 0.02 & echo $! > "$1/grandchild.pid"; wait' child "$1" &
            echo $! > "$1/child.pid"
            /bin/sleep 60 &
            echo $! > "$1/root-sleeper.pid"
            echo ready
            wait
            """,
            "exiting-child" => """
            #!/bin/sh
            echo $$ > "$1/root.pid"
            /bin/sh -c 'exit 0' child "$1" &
            echo $! > "$1/child.pid"
            /bin/sleep 60 &
            echo $! > "$1/root-sleeper.pid"
            echo ready
            wait
            """,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await File.WriteAllTextAsync(script, scriptContents, TestContext.Current.CancellationToken);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DownKyi.MacOS.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
        startInfo.ArgumentList.Add("fixture-legacy-tree-kill");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(directory);
        using var supervisor = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The legacy tree-kill supervisor did not start.");
        try
        {
            Assert.StartsWith("legacy-kill-start pid=", await supervisor.StandardOutput
                .ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.StartsWith("legacy-kill-returned pid=", await supervisor.StandardOutput
                .ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            await supervisor.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(0, supervisor.ExitCode);
        }
        finally
        {
            foreach (var marker in new[] { "grandchild.pid", "child.pid", "root-sleeper.pid", "root.pid" })
            {
                var markerPath = Path.Combine(directory, marker);
                if (File.Exists(markerPath) &&
                    int.TryParse(await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken),
                        CultureInfo.InvariantCulture, out var pid))
                {
                    try
                    {
                        using var descendant = Process.GetProcessById(pid);
                        if (!descendant.HasExited)
                        {
                            descendant.Kill();
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A fixture process may have exited after the assertion.
                    }
                }
            }

            if (!supervisor.HasExited)
            {
                supervisor.Kill();
            }

            await supervisor.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Directory.Delete(directory, recursive: true);
        }
    }
}
