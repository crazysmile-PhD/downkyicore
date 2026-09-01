using System.Diagnostics;
using System.Text.Json;
using DownKyi.CentralTestRunner;

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
            var expectedIdentity =
                $"fixture-ready pid={result.RootPid} start={result.RootStartTimeUtc:O}";
            Assert.Contains(
                expectedIdentity,
                report.GetProperty("StdoutTail").GetString(),
                StringComparison.Ordinal);

            var events = report.GetProperty("Events")
                .EnumerateArray()
                .Select(item => item.GetProperty("Event").GetString())
                .ToArray();
            Assert.Contains("process_start", events);
            Assert.Contains("timeout", events);
            Assert.Contains("bounded_stop_requested", events);
            Assert.Contains("process_exit", events);
            Assert.Contains("cleanup_completed", events);
            Assert.Contains("final_snapshot", events);
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

    private static ProcessStartInfo CreateFixtureStartInfo(string fixture)
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
        return startInfo;
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
