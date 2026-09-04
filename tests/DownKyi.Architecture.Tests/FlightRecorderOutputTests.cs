using System.Diagnostics;
using System.Text.Json;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class FlightRecorderOutputTests
{
    [Fact]
    public async Task UnterminatedOversizedOutputIsDiscardedWithoutUnboundedEvidence()
    {
        const string secret = "fixture-long-line-secret";
        var evidenceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-flight-recorder-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.long-line.slice",
                    "fixture.long-line.test",
                    CreateFixtureStartInfo(secret),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory),
                CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            var artifact = await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
            Assert.True(new FileInfo(result.EvidencePath).Length < 16_384);
            using var document = JsonDocument.Parse(artifact);
            Assert.Contains(
                "[output line exceeded 8192 characters and was discarded]",
                document.RootElement.GetProperty("StdoutTail").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateFixtureStartInfo(string secret)
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
        startInfo.ArgumentList.Add("fixture-long-line");
        startInfo.ArgumentList.Add(secret);
        return startInfo;
    }
}
