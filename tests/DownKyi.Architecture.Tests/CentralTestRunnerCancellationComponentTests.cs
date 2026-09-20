using System.ComponentModel;
using System.Diagnostics;
using DownKyi.CentralTestRunner;
using DownKyi.TestInfrastructure;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerCancellationComponentTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void BuildInvocationDisablesPersistentBuildServers()
    {
        var startInfo = BuildProcessRunner.CreateBuildStartInfo(
            "tests/Fixture.Tests/Fixture.Tests.csproj",
            "Release",
            noRestore: true);

        Assert.Contains("--disable-build-servers", startInfo.ArgumentList);
    }

    [Fact]
    public async Task BuildRunnerReturnsTheOwnedChildExitCodeOnNormalCompletion()
    {
        var exitCode = await BuildProcessRunner.RunAsync(
            CreateFixtureStartInfo("fixture-pass"),
            TestContext.Current.CancellationToken,
            TestTimeout).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task BuildRunnerReportsOwnedChildStartupFailure()
    {
        var startInfo = new ProcessStartInfo(
            Path.Combine(Path.GetTempPath(), $"missing-build-{Guid.NewGuid():N}"))
        {
            UseShellExecute = false
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildProcessRunner.RunAsync(
                startInfo,
                TestContext.Current.CancellationToken,
                TestTimeout)).ConfigureAwait(true);

        Assert.Contains("did not launch", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationshipSnapshotCommandSuccessReturnsAParentMap()
    {
        var parentIds = await ProcessTreeSnapshot.ReadParentIdsAsync(
            CreateFixtureStartInfo("fixture-pass"),
            TestTimeout).ConfigureAwait(true);

        Assert.NotNull(parentIds);
    }

    [Fact]
    public async Task RelationshipSnapshotCommandTimeoutIsTyped()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => ProcessTreeSnapshot.ReadParentIdsAsync(
                CreateFixtureStartInfo("fixture-hold"),
                TimeSpan.Zero)).ConfigureAwait(true);

        Assert.Contains("bounded cleanup window", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationshipSnapshotCommandFailureIsTyped()
    {
        var startInfo = CreateDotNetStartInfo();
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(
            Path.Combine(Path.GetTempPath(), $"missing-snapshot-helper-{Guid.NewGuid():N}.dll"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProcessTreeSnapshot.ReadParentIdsAsync(startInfo, TestTimeout)).ConfigureAwait(true);

        Assert.Contains("snapshot failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationshipSnapshotDefaultBackendCapturesTheLiveRoot()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);

                var snapshot = await ProcessTreeSnapshot.CaptureAsync(fixture.Id, TestTimeout)
                    .ConfigureAwait(true);

                Assert.Contains(snapshot.Processes, process => process.Pid == fixture.Id);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task DiagnosticSnapshotFailureDoesNotPreventKill()
    {
        await AssertSnapshotFailureStillKillsAsync(
            new InvalidOperationException("intentional diagnostic failure")).ConfigureAwait(true);
    }

    [Fact]
    public async Task DiagnosticSnapshotTimeoutDoesNotPreventKill()
    {
        await AssertSnapshotFailureStillKillsAsync(
            new TimeoutException("intentional diagnostic timeout")).ConfigureAwait(true);
    }

    [Fact]
    public async Task BuildSnapshotNeverReturnsDoesNotBlockTermination()
    {
        OwnedProcessScope? scope = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                scope = await StartHoldingScopeAsync().ConfigureAwait(true);
                var clock = Stopwatch.StartNew();
                var failure = await Record.ExceptionAsync(
                    () => BuildProcessRunner.CleanupAfterCancellationAsync(
                        scope,
                        TimeSpan.FromSeconds(2),
                        (_, _) => new TaskCompletionSource<FinalProcessSnapshot>().Task))
                    .ConfigureAwait(true);
                clock.Stop();

                Assert.True(failure is TimeoutException,
                    $"cleanup result={failure?.GetType().Name ?? "success"}, elapsed={clock.Elapsed}, host exited={scope.Host.HasExited}");
                Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(2500));
                Assert.True(scope.Host.HasExited);
            },
            () => StopScopeAsync(scope)).ConfigureAwait(true);
    }

    [Fact]
    public async Task SnapshotTimeoutRemainsPrimaryWhenFilesystemCleanupAlsoFails()
    {
        var exception = await Record.ExceptionAsync(
            () => FailurePreservingTestCleanup.RunAsync(
                () => Task.FromException(new TimeoutException("snapshot timeout")),
                () => Task.FromException(new IOException("directory sharing violation"))))
            .ConfigureAwait(true);

        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.IsType<TimeoutException>(aggregate.InnerExceptions[0]);
        Assert.IsType<IOException>(aggregate.InnerExceptions[1]);
    }

    [Fact]
    public async Task ExitCodeMappingReturns130AfterSuccessfulCancellationCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.RunCommandAsync(
            [],
            (_, token) => Task.FromCanceled<int>(token),
            cancellation.Token).ConfigureAwait(true);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task ExitCodeMappingReturns2ForCleanupFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.RunCommandAsync(
            [],
            (_, _) => Task.FromException<int>(new TimeoutException("snapshot timeout")),
            cancellation.Token)
            .ConfigureAwait(true);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task ExitCodeMappingReturns2ForLiveProcessIdentityFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.RunCommandAsync(
            [],
            (_, _) => Task.FromException<int>(new Win32Exception("live process identity failed")),
            cancellation.Token)
            .ConfigureAwait(true);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task ExitCodeMappingReturns2ForDirectoryRundownTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.RunCommandAsync(
            [],
            (_, _) => Task.FromException<int>(
                new DirectoryResourceRundownTimeoutException(
                    "fixture-directory",
                    TimeSpan.Zero,
                    32)),
            cancellation.Token).ConfigureAwait(true);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task FilesystemTeardownDeletesFixtureDirectoryAfterProcessCleanup()
    {
        var fixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-runner-filesystem-{Guid.NewGuid():N}");
        OwnedProcessScope? scope = null;
        Directory.CreateDirectory(fixtureDirectory);
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                var startInfo = CreateFixtureStartInfo("fixture-hold");
                startInfo.WorkingDirectory = fixtureDirectory;
                scope = await StartHoldingScopeAsync(startInfo).ConfigureAwait(true);

                await BuildProcessRunner.CleanupAfterCancellationAsync(
                    scope,
                    TestTimeout,
                    cleanupResourceDirectory: fixtureDirectory)
                    .ConfigureAwait(true);
                scope.Dispose();
                scope = null;
                Directory.Delete(fixtureDirectory);

                Assert.False(Directory.Exists(fixtureDirectory));
            },
            async () =>
            {
                await StopScopeAsync(scope).ConfigureAwait(true);
                if (Directory.Exists(fixtureDirectory))
                {
                    Directory.Delete(fixtureDirectory);
                }
            }).ConfigureAwait(true);
    }

    private static async Task AssertSnapshotFailureStillKillsAsync(Exception snapshotFailure)
    {
        OwnedProcessScope? scope = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                scope = await StartHoldingScopeAsync().ConfigureAwait(true);

                var observedFailure = await Record.ExceptionAsync(
                    () => BuildProcessRunner.CleanupAfterCancellationAsync(
                        scope,
                        TestTimeout,
                        (_, _) => Task.FromException<FinalProcessSnapshot>(snapshotFailure)))
                    .ConfigureAwait(true);

                Assert.Same(snapshotFailure, observedFailure);
                Assert.True(scope.Host.HasExited);
            },
            () => StopScopeAsync(scope)).ConfigureAwait(true);
    }

    private static Task<Process> StartHoldingFixtureAsync()
    {
        return StartFixtureAsync(CreateFixtureStartInfo("fixture-hold"));
    }

    private static async Task<OwnedProcessScope> StartHoldingScopeAsync(ProcessStartInfo? startInfo = null)
    {
        var scope = await OwnedProcessScope.StartAsync(
            startInfo ?? CreateFixtureStartInfo("fixture-hold"),
            TestTimeout).ConfigureAwait(false);
        try
        {
            var readyLine = await scope.Host.StandardOutput.ReadLineAsync()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            Assert.StartsWith("fixture-ready pid=", readyLine, StringComparison.Ordinal);
            return scope;
        }
        catch
        {
            await StopScopeAsync(scope).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Process> StartFixtureAsync(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            var readyLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            Assert.StartsWith("fixture-ready pid=", readyLine, StringComparison.Ordinal);
            return process;
        }
        catch
        {
            await StopFixtureAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateFixtureStartInfo(string fixtureCommand)
    {
        var startInfo = CreateDotNetStartInfo();
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(
            Path.Combine(AppContext.BaseDirectory, "DownKyi.Architecture.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add(typeof(FlightRecorderExecution).Assembly.Location);
        startInfo.ArgumentList.Add(fixtureCommand);
        return startInfo;
    }

    private static ProcessStartInfo CreateDotNetStartInfo()
    {
        return new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static async Task StopFixtureAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TestTimeout).ConfigureAwait(false);
        }
    }

    private static async Task StopScopeAsync(OwnedProcessScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        using (scope)
        {
            await scope.TerminateAsync(new CleanupDeadline(TestTimeout)).ConfigureAwait(false);
        }
    }
}
