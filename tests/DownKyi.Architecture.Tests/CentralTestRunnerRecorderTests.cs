using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;
using DownKyi.TestInfrastructure;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerRecorderTests
{
    [Fact]
    public async Task CanceledTestProcessPreservesIdentityCleanupSnapshotAndGuidance()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        var processStartPersistenceFailed = 0;
        try
        {
            Task PersistAsync(string path, string json, CancellationToken cancellationToken)
            {
                if (LastEventMatches(json, "process_start") &&
                    Interlocked.Exchange(ref processStartPersistenceFailed, 1) == 0)
                {
                    return FailProcessStartPersistenceAsync(path, json, cancellationToken);
                }

                return File.WriteAllTextAsync(path, json, cancellationToken);
            }

            static async Task FailProcessStartPersistenceAsync(
                string path,
                string json,
                CancellationToken cancellationToken)
            {
                // Reproduce the FileShare.Read collision from the former JSON readiness probe.
                using var reader = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
                using var persistedReport = await JsonDocument.ParseAsync(
                    reader,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var persistedEvents = persistedReport.RootElement
                    .GetProperty("Events")
                    .EnumerateArray()
                    .ToArray();
                Assert.DoesNotContain(
                    persistedEvents,
                    item => string.Equals(
                        item.GetProperty("Event").GetString(),
                        "process_start",
                        StringComparison.Ordinal));
                var lastPersistedEvent = persistedEvents[^1];
                Assert.Equal(
                    "scope_launch_phase",
                    lastPersistedEvent.GetProperty("Event").GetString());
                Assert.Equal(
                    "handshake_received",
                    lastPersistedEvent.GetProperty("Detail").GetString());

                if (OperatingSystem.IsWindows())
                {
                    await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new IOException("process_start diagnostic persistence failed");
            }

            var (result, fixturePid) = await RunCanceledFixtureAsync(
                "fixture.cancellation.slice",
                "fixture.cancellation.test",
                TimeSpan.FromSeconds(3),
                evidenceDirectory,
                recorderPersistence: PersistAsync);

            Assert.Equal(130, result.ExitCode);
            Assert.Equal(1, processStartPersistenceFailed);
            Assert.Equal(fixturePid, result.RootPid);
            Assert.True(result.RootPid > 0);
            Assert.NotNull(result.RootStartTimeUtc);
            Assert.True(File.Exists(result.EvidencePath));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken));
            var report = document.RootElement;
            Assert.Equal("fixture.cancellation.slice", report.GetProperty("SliceIdentity").GetString());
            Assert.Equal("fixture.cancellation.test", report.GetProperty("TestIdentity").GetString());
            Assert.Equal(result.RootPid, report.GetProperty("RootProcess").GetProperty("Pid").GetInt32());
            Assert.Equal(
                result.RootStartTimeUtc,
                report.GetProperty("RootProcess").GetProperty("StartTimeUtc").GetDateTimeOffset());
            var events = report.GetProperty("Events")
                .EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString())
                .ToArray();
            Assert.Contains("process_start", events);
            Assert.Contains("recorder_persistence_failed", events);
            Assert.Contains("cancellation", events);
            Assert.Contains("bounded_stop_requested", events);
            Assert.Contains("process_exit", events);
            Assert.Contains("cleanup_completed", events);
            Assert.Contains(
                events,
                eventName => eventName is "final_snapshot" or "final_snapshot_failed");
            var snapshotIndex = Array.FindIndex(
                events,
                eventName => eventName is "final_snapshot" or "final_snapshot_failed");
            var stopIndex = Array.IndexOf(events, "bounded_stop_requested");
            Assert.InRange(snapshotIndex, 0, stopIndex - 1);
            Assert.Contains(
                report.GetProperty("Events").EnumerateArray(),
                item => string.Equals(
                            item.GetProperty("Event").GetString(),
                            "process_exit",
                            StringComparison.Ordinal) &&
                        item.TryGetProperty("ExitCode", out _));

            var snapshot = report.GetProperty("FinalSnapshot");
            Assert.Contains(
                "absence is not proof",
                snapshot.GetProperty("Completeness").GetString(),
                StringComparison.Ordinal);
            if (events.Contains("final_snapshot_failed", StringComparer.Ordinal))
            {
                Assert.False(string.IsNullOrWhiteSpace(snapshot.GetProperty("Error").GetString()));
            }
            Assert.Equal(
                FlightRecorder.DiagnosticGuidance,
                report.GetProperty("DiagnosticGuidance").GetString());
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledStartupDiagnosticPersistenceCleansUpLaunchedProcess()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        var markerPath = Path.Combine(evidenceDirectory, $"fixture-{Guid.NewGuid():N}.pid");
        var persistenceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handshakeWriteIntercepted = 0;
        int? fixturePid = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            async Task PersistAsync(string path, string json, CancellationToken cancellationToken)
            {
                if (LastEventMatches(json, "scope_launch_phase", "handshake_received") &&
                    Interlocked.Exchange(ref handshakeWriteIntercepted, 1) == 0)
                {
                    persistenceEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            }

            var run = FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.persistence-cancellation.slice",
                    "fixture.persistence-cancellation.test",
                    CreateFixtureStartInfo("fixture-hold-marker", markerPath),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    RecorderPersistence: PersistAsync),
                cancellation.Token);

            fixturePid = await WaitForProcessMarkerOrOwnerCompletionAsync(
                markerPath,
                run,
                result =>
                    $"Process execution returned exit code {result.ExitCode} before the fixture process marker was observed.");
            await persistenceEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run)
                .WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                using var fixture = Process.GetProcessById(fixturePid.Value);
                await fixture.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            catch (ArgumentException)
            {
                // The startup cleanup completed before the test opened the process handle.
            }
            Assert.False(IsProcessAlive(fixturePid.Value));
        }
        finally
        {
            await cancellation.CancelAsync();
            if (fixturePid is not null)
            {
                StopFixtureProcessIfAlive(fixturePid.Value);
            }
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HandshakeDiagnosticPersistenceFailurePreservesEstablishedRootIdentity()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        var handshakeWriteIntercepted = 0;
        try
        {
            Task PersistAsync(string path, string json, CancellationToken cancellationToken)
            {
                if (LastEventMatches(json, "scope_launch_phase", "handshake_received") &&
                    Interlocked.Exchange(ref handshakeWriteIntercepted, 1) == 0)
                {
                    return Task.FromException(
                        new IOException("handshake_received diagnostic persistence failed"));
                }

                return File.WriteAllTextAsync(path, json, cancellationToken);
            }

            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.persistence-failure.slice",
                    "fixture.persistence-failure.test",
                    CreateFixtureStartInfo("fixture-pass"),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    RecorderPersistence: PersistAsync),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, handshakeWriteIntercepted);
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.RootPid > 0);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken));
            var report = document.RootElement;
            Assert.Equal(result.RootPid, report.GetProperty("RootProcess").GetProperty("Pid").GetInt32());
            var events = report.GetProperty("Events").EnumerateArray().ToArray();
            Assert.Contains(events, item => string.Equals(
                item.GetProperty("Event").GetString(),
                "process_start",
                StringComparison.Ordinal));
            Assert.Contains(events, item =>
                string.Equals(
                    item.GetProperty("Event").GetString(),
                    "recorder_persistence_failed",
                    StringComparison.Ordinal) &&
                item.GetProperty("Detail").GetString()!.Contains(
                    "handshake_received diagnostic persistence failed",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(events, item => string.Equals(
                item.GetProperty("Event").GetString(),
                "process_start_failed",
                StringComparison.Ordinal));

            await FlightRecorderExecution.DiscardAsync(result);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotFailureDoesNotPreventCleanupOrActionableFailureEvidence()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var (result, _) = await RunCanceledFixtureAsync(
                "fixture.snapshot-failure.slice",
                "fixture.snapshot-failure.test",
                TimeSpan.FromSeconds(3),
                evidenceDirectory,
                snapshotCapture: (_, _) => Task.FromException<FinalProcessSnapshot>(
                    new IOException("snapshot token=fixture-snapshot-secret")));

            Assert.NotEqual(0, result.ExitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken));
            var report = document.RootElement;
            var events = report.GetProperty("Events")
                .EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString())
                .ToArray();
            var snapshotIndex = Array.IndexOf(events, "final_snapshot_failed");
            var stopIndex = Array.IndexOf(events, "bounded_stop_requested");
            Assert.InRange(snapshotIndex, 0, stopIndex - 1);
            Assert.Contains("cleanup_completed", events);
            Assert.Contains(
                "absence is not proof",
                report.GetProperty("FinalSnapshot").GetProperty("Completeness").GetString(),
                StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(
                report.GetProperty("FinalSnapshot").GetProperty("Error").GetString()));
            Assert.Equal(
                FlightRecorder.DiagnosticGuidance,
                report.GetProperty("DiagnosticGuidance").GetString());
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotNeverReturnsDoesNotBlockTerminationAndCleanupBudget()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var (result, _) = await RunCanceledFixtureAsync(
                "fixture.snapshot-never-returns.slice",
                "fixture.snapshot-never-returns.test",
                TimeSpan.FromSeconds(2),
                evidenceDirectory,
                snapshotCapture: (_, _) => new TaskCompletionSource<FinalProcessSnapshot>().Task);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(IsProcessAlive(result.RootPid));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken));
            var events = document.RootElement.GetProperty("Events")
                .EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString())
                .ToArray();
            Assert.Contains("final_snapshot_failed", events);
            Assert.Contains("bounded_stop_requested", events);
            Assert.Contains("cleanup_completed", events);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BlockedStderrForwardingCannotDelayMandatoryTermination()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        using var blockedError = new BlockingTextWriter();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var run = FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.stderr-backpressure.slice",
                    "fixture.stderr-backpressure.test",
                    CreateFixtureStartInfo("fixture-stderr-hold"),
                    TimeSpan.FromSeconds(1),
                    evidenceDirectory,
                    SnapshotCapture: CaptureControlledSnapshotAsync,
                    ErrorDestination: blockedError),
                cancellation.Token);
            await blockedError.Entered.WaitAsync(TestContext.Current.CancellationToken);

            await cancellation.CancelAsync();
            var result = await run.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, result.ExitCode);
            Assert.False(IsProcessAlive(result.RootPid));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath, TestContext.Current.CancellationToken));
            var events = document.RootElement.GetProperty("Events").EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString()).ToArray();
            Assert.Contains("bounded_stop_requested", events);
            Assert.Contains("cleanup_failed", events);
        }
        finally
        {
            blockedError.Release();
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RecorderPersistenceDeadlineDoesNotAbandonAnOpenFile()
    {
        var directory = CreateEvidenceDirectory();
        var path = Path.Combine(directory, "owned-write.json");
        var persistenceStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var persistence = FlightRecorder.PersistWithinDeadlineAsync(
                new CleanupDeadline(TimeSpan.FromMilliseconds(100)),
                async cancellationToken =>
                {
                    try
                    {
                        using var stream = new FileStream(
                            path, FileMode.Create, FileAccess.Write, FileShare.Read,
                            bufferSize: 1, FileOptions.Asynchronous);
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        persistenceStopped.TrySetResult();
                    }
                });

            await Assert.ThrowsAsync<TimeoutException>(() => persistence)
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(persistenceStopped.Task.IsCompleted);
            File.Delete(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SensitiveEvidenceIsRedactedAtEveryRecorderTextBoundary()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var (result, _) = await RunCanceledFixtureAsync(
                "fixture.redaction.slice",
                "fixture.redaction.test token=fixture-identity-secret",
                TimeSpan.FromSeconds(3),
                evidenceDirectory,
                markerPath =>
                {
                    var startInfo = CreateFixtureStartInfo(
                        "fixture-sensitive-hold",
                        "fixture-bearer-secret",
                        "fixture-url-secret",
                        "fixture-account-secret",
                        "fixture-cookie-secret",
                        markerPath);
                    startInfo.WorkingDirectory = Directory.GetCurrentDirectory();
                    return startInfo;
                },
                (_, _) => Task.FromException<FinalProcessSnapshot>(
                    new IOException(
                        $"snapshot access_token=fixture-snapshot-secret url=https://example.invalid/private?token=fixture-query-secret path={userProfile}")));
            await result.Recorder.RecordAsync(
                "external_detail",
                detail: "accountId=fixture-event-account-secret token=fixture-event-token-secret",
                cancellationToken: TestContext.Current.CancellationToken);

            var artifact = await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken);
            foreach (var secret in new[]
                     {
                         "fixture-bearer-secret",
                         "fixture-url-secret",
                         "fixture-account-secret",
                         "fixture-cookie-secret",
                         "fixture-snapshot-secret",
                         "fixture-query-secret",
                         "fixture-event-account-secret",
                         "fixture-event-token-secret",
                         "fixture-identity-secret"
                     })
            {
                Assert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
            }
            Assert.DoesNotContain(userProfile, artifact, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(artifact);
            var report = document.RootElement;
            Assert.Contains(
                "token=<redacted>",
                report.GetProperty("TestIdentity").GetString(),
                StringComparison.Ordinal);
            var redactedEvidence = string.Join(
                Environment.NewLine,
                new[]
                {
                    report.GetProperty("StdoutTail").GetString(),
                    report.GetProperty("StderrTail").GetString(),
                    report.GetProperty("FinalSnapshot").GetProperty("Error").GetString()
                }.Concat(report.GetProperty("Events")
                    .EnumerateArray()
                    .Where(item => item.TryGetProperty("Detail", out _))
                    .Select(item => item.GetProperty("Detail").GetString())));
            Assert.Contains("<redacted>", redactedEvidence, StringComparison.Ordinal);
            Assert.Contains("<redacted-url>", redactedEvidence, StringComparison.Ordinal);
            Assert.Contains("<user-profile>", redactedEvidence, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public void ParentIdParserAcceptsWindowsAndUnixSnapshotRows()
    {
        var parentIds = ProcessTreeSnapshot.ParseParentIds(
            "1000|1\n  1234    1000\n2345\t1234\ninvalid-row\n");

        Assert.Equal(1, parentIds[1000]);
        Assert.Equal(1000, parentIds[1234]);
        Assert.Equal(1234, parentIds[2345]);
        Assert.Equal(3, parentIds.Count);
    }

    [Fact]
    public async Task ProcessMarkerWaitFailsWhenOwnerCompletesFirst()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-missing-fixture-{Guid.NewGuid():N}.pid");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await WaitForProcessMarkerOrOwnerCompletionAsync(
                markerPath,
                Task.FromResult(17),
                exitCode =>
                    $"Build process returned exit code {exitCode} before the fixture process marker was observed.")
                .ConfigureAwait(false));

        Assert.Equal(
            "Build process returned exit code 17 before the fixture process marker was observed.",
            failure.Message);
    }

    [Fact]
    public async Task BuildCancellationStopsTheLiveOwnedProcessBeforeReturning()
    {
        var directory = CreateEvidenceDirectory();
        var markerPath = Path.Combine(directory, "build-process.pid");
        int? processId = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var build = BuildProcessRunner.RunAsync(
                CreateFixtureStartInfo("fixture-hold-marker", markerPath),
                cancellation.Token,
                TimeSpan.FromSeconds(3),
                captureSnapshotAsync: CaptureControlledSnapshotAsync);
            processId = await WaitForProcessMarkerOrOwnerCompletionAsync(
                markerPath,
                build,
                exitCode =>
                    $"Build process returned exit code {exitCode} before the fixture process marker was observed.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);

            Assert.False(IsProcessAlive(processId.Value));
        }
        finally
        {
            if (processId is not null)
            {
                StopFixtureProcessIfAlive(processId.Value);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataDiscoveredProjectDeletesStaleTrxBeforeBuildFailure()
    {
        var repositoryRoot = CreateEvidenceDirectory();
        try
        {
            var projectDirectory = Path.Combine(repositoryRoot, "tests", "Fixture.Tests");
            var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
            var resultsDirectory = Path.Combine(repositoryRoot, "results");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(policyDirectory);
            Directory.CreateDirectory(resultsDirectory);
            var projectPath = Path.Combine(projectDirectory, "Fixture.Tests.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project DefaultTargets="Build">
                  <PropertyGroup>
                    <DownKyiTestPlatforms>Windows;Linux;macOS</DownKyiTestPlatforms>
                  </PropertyGroup>
                  <Target Name="Build">
                    <Error Text="intentional fixture build failure" />
                  </Target>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(policyDirectory, "test-runner-policy.json"),
                """{"schemaVersion":1,"projects":[]}""",
                TestContext.Current.CancellationToken);
            var trxPath = Path.Combine(resultsDirectory, "fixture.trx");
            await File.WriteAllTextAsync(
                trxPath,
                "<TestRun><ResultSummary><Counters executed=\"1\" failed=\"0\" /></ResultSummary></TestRun>",
                TestContext.Current.CancellationToken);

            var discovered = TestProjectCatalog.DiscoverProjects(repositoryRoot);
            var definition = Assert.Single(discovered);
            Assert.Equal("tests/Fixture.Tests/Fixture.Tests.csproj", definition.Project);
            Assert.Equal(["Windows", "Linux", "macOS"], definition.Platforms);

            var exitCode = await CentralTestCommand.RunAsync(
                [
                    "run-project",
                    "--repository-root", repositoryRoot,
                    "--project", definition.Project,
                    "--configuration", "Release",
                    "--no-restore",
                    "--results-directory", resultsDirectory,
                    "--trx-name", "fixture.trx"
                ],
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(trxPath));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataDiscoveryRejectsUnknownPlatformName()
    {
        var repositoryRoot = CreateEvidenceDirectory();
        try
        {
            var projectDirectory = Path.Combine(repositoryRoot, "tests", "Fixture.Tests");
            var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(policyDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
                """
                <Project>
                  <PropertyGroup>
                    <DownKyiTestPlatforms>Windows;Linuz;macOS</DownKyiTestPlatforms>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(policyDirectory, "test-runner-policy.json"),
                """{"schemaVersion":1,"projects":[]}""",
                TestContext.Current.CancellationToken);

            var exception = Assert.Throws<InvalidDataException>(
                () => TestProjectCatalog.DiscoverProjects(repositoryRoot));
            Assert.Contains("Linuz", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataDiscoveryRejectsNestedConditionalPlatformDeclaration()
    {
        var repositoryRoot = CreateEvidenceDirectory();
        try
        {
            var projectDirectory = Path.Combine(repositoryRoot, "tests", "Fixture.Tests");
            var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(policyDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
                """
                <Project>
                  <Choose>
                    <When Condition="'$(OS)' == 'Windows_NT'">
                      <PropertyGroup>
                        <DownKyiTestPlatforms>Windows</DownKyiTestPlatforms>
                      </PropertyGroup>
                    </When>
                  </Choose>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(policyDirectory, "test-runner-policy.json"),
                """{"schemaVersion":1,"projects":[]}""",
                TestContext.Current.CancellationToken);

            var exception = Assert.Throws<InvalidDataException>(
                () => TestProjectCatalog.DiscoverProjects(repositoryRoot));
            Assert.Contains("unconditionally", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MissingRootStartTimeDoesNotOverridePassingProcess()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.pass.slice",
                    "fixture.pass.test",
                    CreateFixtureStartInfo("fixture-pass"),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    RootStartTimeReader: _ => throw new InvalidOperationException(
                        "fixture start time unavailable")),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Null(result.RootStartTimeUtc);
            await FlightRecorderExecution.DiscardAsync(result);
            Assert.Empty(Directory.EnumerateFiles(evidenceDirectory));
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateFixtureStartInfo(string fixture, params string[] arguments)
    {
        var runnerAssembly = typeof(FlightRecorderExecution).Assembly.Location;
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory,
            "DownKyi.Architecture.Tests.runtimeconfig.json");
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
        startInfo.ArgumentList.Add(runnerAssembly);
        startInfo.ArgumentList.Add(fixture);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<(ProcessExecutionResult Result, int FixturePid)> RunCanceledFixtureAsync(
        string sliceIdentity,
        string testIdentity,
        TimeSpan cleanupTimeout,
        string evidenceDirectory,
        Func<string, ProcessStartInfo>? startInfoFactory = null,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>>? snapshotCapture = null,
        Func<string, string, CancellationToken, Task>? recorderPersistence = null)
    {
        var markerPath = Path.Combine(evidenceDirectory, $"fixture-{Guid.NewGuid():N}.pid");
        var startupReady = new TaskCompletionSource<ProcessExecutionStartup>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startInfo = startInfoFactory is null
            ? CreateFixtureStartInfo("fixture-hold-marker", markerPath)
            : startInfoFactory(markerPath);
        using var cancellation = new CancellationTokenSource();
        var run = FlightRecorderExecution.RunAsync(
            new ProcessExecutionRequest(
                sliceIdentity,
                testIdentity,
                startInfo,
                cleanupTimeout,
                evidenceDirectory,
                snapshotCapture,
                RecorderPersistence: recorderPersistence,
                StartupReady: startup => startupReady.TrySetResult(startup)),
            cancellation.Token);

        await WaitForSignalOrRunCompletionAsync(
            startupReady.Task,
            run,
            "authoritative process startup").ConfigureAwait(false);
        var startup = await startupReady.Task.ConfigureAwait(false);
        var fixturePid = await WaitForProcessMarkerOrOwnerCompletionAsync(
            markerPath,
            run,
            result =>
                $"Process execution returned exit code {result.ExitCode} before the fixture process marker was observed.")
            .ConfigureAwait(false);
        Assert.Equal(fixturePid, startup.RootPid);
        await cancellation.CancelAsync().ConfigureAwait(false);
        var result = await run.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (result, fixturePid);
    }

    private static async Task WaitForSignalOrRunCompletionAsync(
        Task signal,
        Task<ProcessExecutionResult> run,
        string signalName)
    {
        var completed = await Task.WhenAny(signal, run)
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, run))
        {
            var result = await run.ConfigureAwait(false);
            Assert.Fail(
                $"Process execution returned exit code {result.ExitCode} before {signalName} was observed.");
        }

        await signal.ConfigureAwait(false);
    }

    private static async Task<int> WaitForProcessMarkerOrOwnerCompletionAsync<TOwnerResult>(
        string markerPath,
        Task<TOwnerResult> owner,
        Func<TOwnerResult, string> describeOwnerCompletion)
    {
        while (true)
        {
            if (File.Exists(markerPath))
            {
                try
                {
                    if (int.TryParse(
                        await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken)
                            .ConfigureAwait(false),
                        CultureInfo.InvariantCulture,
                        out var processId))
                    {
                        return processId;
                    }
                }
                catch (IOException)
                {
                    // The fixture created the marker but has not closed its write handle yet.
                }
            }

            var retry = Task.Delay(20, TestContext.Current.CancellationToken);
            var completed = await Task.WhenAny(retry, owner)
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, owner))
            {
                var result = await owner.ConfigureAwait(false);
                throw new InvalidOperationException(describeOwnerCompletion(result));
            }

            await retry.ConfigureAwait(false);
        }
    }

    private static bool LastEventMatches(string json, string eventName, string? detail = null)
    {
        using var report = JsonDocument.Parse(json);
        var events = report.RootElement.GetProperty("Events").EnumerateArray().ToArray();
        var lastEvent = events[^1];
        return string.Equals(lastEvent.GetProperty("Event").GetString(), eventName, StringComparison.Ordinal) &&
               (detail is null ||
                lastEvent.TryGetProperty("Detail", out var actualDetail) &&
                string.Equals(actualDetail.GetString(), detail, StringComparison.Ordinal));
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

    private static void StopFixtureProcessIfAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
        catch (ArgumentException)
        {
            // The focused cancellation path already stopped the fixture.
        }
    }

    private static string CreateEvidenceDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-flight-recorder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task<FinalProcessSnapshot> CaptureControlledSnapshotAsync(int _, TimeSpan __) =>
        Task.FromResult(new FinalProcessSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Completeness = "Controlled successful snapshot.",
            Processes = []
        });

    private sealed class BlockingTextWriter : TextWriter
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(false);

        public Task Entered => entered.Task;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            entered.TrySetResult();
            release.Wait();
            return Task.CompletedTask;
        }

        public void Release() => release.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Set();
                release.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
