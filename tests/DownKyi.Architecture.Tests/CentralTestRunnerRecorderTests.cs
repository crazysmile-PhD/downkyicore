using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using DownKyi.TestInfrastructure;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerRecorderTests
{
    [Fact]
    public async Task TimedOutTestProcessPreservesIdentityCleanupSnapshotAndGuidance()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var request = new ProcessExecutionRequest(
                "fixture.timeout.slice",
                "fixture.timeout.test",
                CreateFixtureStartInfo("fixture-hold"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
                evidenceDirectory);

            var result = await FlightRecorderExecution.RunAsync(request, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.RootPid > 0);
            Assert.NotEqual(default, result.RootStartTimeUtc);
            Assert.True(File.Exists(result.EvidencePath));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken));
            var report = document.RootElement;
            Assert.Equal("fixture.timeout.slice", report.GetProperty("SliceIdentity").GetString());
            Assert.Equal("fixture.timeout.test", report.GetProperty("TestIdentity").GetString());
            Assert.Equal(result.RootPid, report.GetProperty("RootProcess").GetProperty("Pid").GetInt32());
            Assert.Equal(
                result.RootStartTimeUtc,
                report.GetProperty("RootProcess").GetProperty("StartTimeUtc").GetDateTimeOffset());
            var standardOutput = report.GetProperty("StdoutTail").GetString() ?? string.Empty;
            var identityLine = standardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("fixture-ready ", StringComparison.Ordinal));
            var identityPrefix = $"fixture-ready pid={result.RootPid} start=";
            Assert.StartsWith(identityPrefix, identityLine, StringComparison.Ordinal);
            var fixtureStartTime = DateTimeOffset.ParseExact(
                identityLine[identityPrefix.Length..],
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            Assert.InRange(
                Math.Abs((fixtureStartTime - result.RootStartTimeUtc).TotalSeconds),
                0,
                1);

            var events = report.GetProperty("Events")
                .EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString())
                .ToArray();
            Assert.Contains("process_start", events);
            Assert.Contains("timeout", events);
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
            Assert.True(snapshot.GetProperty("CapturedAtUtc").GetDateTimeOffset() > result.RootStartTimeUtc);
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
    public async Task SnapshotFailureDoesNotPreventCleanupOrActionableFailureEvidence()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.snapshot-failure.slice",
                    "fixture.snapshot-failure.test",
                    CreateFixtureStartInfo("fixture-hold"),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    (_, _) => Task.FromException<FinalProcessSnapshot>(
                        new IOException("snapshot token=fixture-snapshot-secret"))),
                CancellationToken.None);

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
    public async Task SensitiveEvidenceIsRedactedAtEveryRecorderTextBoundary()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var startInfo = CreateFixtureStartInfo(
                "fixture-sensitive-hold",
                "fixture-bearer-secret",
                "fixture-url-secret",
                "fixture-account-secret",
                "fixture-cookie-secret");
            startInfo.WorkingDirectory = Directory.GetCurrentDirectory();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.redaction.slice",
                    "fixture.redaction.test token=fixture-identity-secret",
                    startInfo,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    (_, _) => Task.FromException<FinalProcessSnapshot>(
                        new IOException(
                            $"snapshot access_token=fixture-snapshot-secret url=https://example.invalid/private?token=fixture-query-secret path={userProfile}"))),
                CancellationToken.None);
            await result.Recorder.RecordAsync(
                "external_detail",
                detail: "accountId=fixture-event-account-secret token=fixture-event-token-secret");

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
    public async Task BuildCancellationStopsTheLiveOwnedProcessBeforeReturning()
    {
        var directory = CreateEvidenceDirectory();
        var markerPath = Path.Combine(directory, "build-process.pid");
        int? processId = null;
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                using var cancellation = new CancellationTokenSource();
                var build = BuildProcessRunner.RunAsync(
                    CreateFixtureStartInfo("fixture-hold-marker", markerPath),
                    cancellation.Token,
                    TimeSpan.FromSeconds(3));
                processId = await WaitForProcessMarkerAsync(markerPath).ConfigureAwait(true);

                await cancellation.CancelAsync().ConfigureAwait(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build).ConfigureAwait(true);

                Assert.False(IsProcessAlive(processId.Value));
            },
            async () =>
            {
                if (processId is not null)
                {
                    await StopFixtureProcessIfAlive(processId.Value).ConfigureAwait(true);
                }
            },
            () => DeleteDirectoryAsync(directory)).ConfigureAwait(true);
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
    public async Task PassingTestProcessDiscardsRecorderArtifact()
    {
        var evidenceDirectory = CreateEvidenceDirectory();
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.pass.slice",
                    "fixture.pass.test",
                    CreateFixtureStartInfo("fixture-pass"),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
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

    private static async Task<int> WaitForProcessMarkerAsync(string markerPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
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

            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The build cancellation fixture did not publish its process identity.");
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

    private static async Task StopFixtureProcessIfAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                await ExternalProcessTestHarness.StopAsync(
                    process,
                    TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
        }
        catch (ArgumentException)
        {
            // The focused cancellation path already stopped the fixture.
        }
    }

    private static Task DeleteDirectoryAsync(string path)
    {
        Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    private static string CreateEvidenceDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-flight-recorder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
