using System.ComponentModel;
using System.Diagnostics;
using DownKyi.CentralTestRunner;

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
    public async Task IdentityObservationReadsStartTimeBeforeWaitingForExit()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var identityRead = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();
                var wait = BuildProcessRunner.WaitForObservedProcessExitAsync(
                    CreateObservedProcess(fixture, expectedStartTime),
                    process =>
                    {
                        identityRead.TrySetResult();
                        return process.StartTime.ToUniversalTime();
                    });

                await identityRead.Task.WaitAsync(TestTimeout).ConfigureAwait(true);
                BuildProcessRunner.KillOwnedProcessTree(fixture);
                await wait.WaitAsync(TestTimeout).ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("Win32Exception")]
    [InlineData("InvalidOperationException")]
    public async Task IdentityObservationTreatsAnExitedProcessReadFailureAsTerminal(string exceptionType)
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();

                await BuildProcessRunner.WaitForObservedProcessExitAsync(
                    CreateObservedProcess(fixture, expectedStartTime),
                    _ =>
                    {
                        BuildProcessRunner.KillOwnedProcessTree(fixture);
                        if (!fixture.WaitForExit((int)TestTimeout.TotalMilliseconds))
                        {
                            throw new TimeoutException("The identity fixture did not exit.");
                        }

                        throw CreateIdentityReadException(exceptionType);
                    }).ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("Win32Exception")]
    [InlineData("InvalidOperationException")]
    public async Task IdentityObservationPreservesReadFailureForALiveProcess(string exceptionType)
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();

                var exception = await Record.ExceptionAsync(
                    () => BuildProcessRunner.WaitForObservedProcessExitAsync(
                        CreateObservedProcess(fixture, expectedStartTime),
                        _ => throw CreateIdentityReadException(exceptionType))).ConfigureAwait(true);

                Assert.IsType(CreateIdentityReadException(exceptionType).GetType(), exception);
                Assert.False(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task IdentityObservationTreatsAMissingPidAsTerminalWhenHasExitedAlsoFails()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();

                await BuildProcessRunner.WaitForObservedProcessExitAsync(
                    CreateObservedProcess(fixture, expectedStartTime),
                    _ =>
                    {
                        BuildProcessRunner.KillOwnedProcessTree(fixture);
                        if (!fixture.WaitForExit((int)TestTimeout.TotalMilliseconds))
                        {
                            throw new TimeoutException("The identity fixture did not exit.");
                        }

                        throw new Win32Exception("identity unavailable");
                    },
                    _ => throw new Win32Exception("exit state unavailable"),
                    _ => false).ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task IdentityObservationPreservesFirstFailureWhenSecondaryObservationsAreInconclusive()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();
                var identityFailure = new Win32Exception("identity unavailable");

                var exception = await Record.ExceptionAsync(
                    () => BuildProcessRunner.WaitForObservedProcessExitAsync(
                        CreateObservedProcess(fixture, expectedStartTime),
                        _ => throw identityFailure,
                        _ => throw new Win32Exception("exit state unavailable"),
                        _ => throw new InvalidOperationException("presence unavailable")))
                    .ConfigureAwait(true);

                Assert.Same(identityFailure, exception);
                Assert.False(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
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
    public async Task KillOwnedProcessTreeStopsTheRoot()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);

                BuildProcessRunner.KillOwnedProcessTree(fixture);
                await BuildProcessRunner.WaitForRootExitAsync(fixture, TestTimeout).ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task RootReapCompletesAfterKill()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                fixture.Kill(entireProcessTree: true);

                await BuildProcessRunner.WaitForRootExitAsync(fixture, TestTimeout).ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task RootReapTimeoutRemainsATypedCleanupFailure()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);

                await Assert.ThrowsAsync<TimeoutException>(
                    () => BuildProcessRunner.WaitForRootExitAsync(fixture, TimeSpan.Zero))
                    .ConfigureAwait(true);

                Assert.False(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    [Fact]
    public async Task DescendantCleanupWaitsForTheOwnedProcessToExit()
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                var expectedStartTime = fixture.StartTime.ToUniversalTime();
                var identityRead = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var wait = BuildProcessRunner.WaitForOwnedProcessesToExitAsync(
                    [CreateObservedProcess(fixture, expectedStartTime)],
                    TestTimeout,
                    _ =>
                    {
                        identityRead.TrySetResult();
                        return expectedStartTime;
                    });

                await identityRead.Task.WaitAsync(TestTimeout).ConfigureAwait(true);
                fixture.Kill(entireProcessTree: true);
                await wait.ConfigureAwait(true);

                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
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
    public async Task FilesystemTeardownDeletesFixtureDirectoryAfterProcessCleanup()
    {
        var fixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-runner-filesystem-{Guid.NewGuid():N}");
        Process? fixture = null;
        Directory.CreateDirectory(fixtureDirectory);
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                var startInfo = CreateFixtureStartInfo("fixture-hold");
                startInfo.WorkingDirectory = fixtureDirectory;
                fixture = await StartFixtureAsync(startInfo).ConfigureAwait(true);

                await BuildProcessRunner.CleanupAfterCancellationAsync(fixture, TestTimeout)
                    .ConfigureAwait(true);
                fixture.Dispose();
                fixture = null;

                Directory.Delete(fixtureDirectory);

                Assert.False(Directory.Exists(fixtureDirectory));
            },
            async () =>
            {
                await StopFixtureAsync(fixture).ConfigureAwait(true);
                if (Directory.Exists(fixtureDirectory))
                {
                    Directory.Delete(fixtureDirectory);
                }
            }).ConfigureAwait(true);
    }

    private static async Task AssertSnapshotFailureStillKillsAsync(Exception snapshotFailure)
    {
        Process? fixture = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);

                var observedFailure = await Record.ExceptionAsync(
                    () => BuildProcessRunner.CleanupAfterCancellationAsync(
                        fixture,
                        TestTimeout,
                        (_, _) => Task.FromException<FinalProcessSnapshot>(snapshotFailure)))
                    .ConfigureAwait(true);

                Assert.Same(snapshotFailure, observedFailure);
                Assert.True(fixture.HasExited);
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);
    }

    private static ObservedProcess CreateObservedProcess(Process process, DateTimeOffset startTimeUtc)
    {
        return new ObservedProcess
        {
            Pid = process.Id,
            ParentPid = Environment.ProcessId,
            StartTimeUtc = startTimeUtc
        };
    }

    private static Exception CreateIdentityReadException(string exceptionType)
    {
        return exceptionType switch
        {
            "Win32Exception" => new Win32Exception("identity unavailable"),
            "InvalidOperationException" => new InvalidOperationException("identity unavailable"),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType))
        };
    }

    private static Task<Process> StartHoldingFixtureAsync()
    {
        return StartFixtureAsync(CreateFixtureStartInfo("fixture-hold"));
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
}
