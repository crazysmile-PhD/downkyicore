using System.Diagnostics;
using System.Text.Json;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerResultBoundaryTests
{
    [Fact]
    public async Task PassingLifecycleNeedsValidTrxBeforeRecorderIsDiscarded()
    {
        var directory = CreateDirectory();
        try
        {
            var result = await RunFixtureAsync(directory);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fixture-pass pid=", result.Recorder.StdoutTail, StringComparison.Ordinal);
            Assert.Contains("fixture-pass stderr", result.Recorder.StderrTail, StringComparison.Ordinal);
            var trxPath = Path.Combine(directory, "passing.trx");
            await File.WriteAllTextAsync(
                trxPath,
                """<TestRun><Counters executed="1" failed="0" /></TestRun>""",
                TestContext.Current.CancellationToken);

            var exitCode = await CentralTestCommand.CompleteProjectResultAsync(
                result, trxPath, "fixture", "passing.trx");

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(result.EvidencePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingTrxRetainsTheOriginalPostExitFailureAsPrimaryEvidence()
    {
        var directory = CreateDirectory();
        try
        {
            var result = await RunFixtureAsync(directory);
            var exitCode = await CentralTestCommand.CompleteProjectResultAsync(
                result, Path.Combine(directory, "missing.trx"), "fixture", "missing.trx");

            Assert.Equal(1, exitCode);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.EvidencePath, TestContext.Current.CancellationToken));
            Assert.Equal(
                "trx_validation_failed",
                report.RootElement.GetProperty("Outcome").GetString());
            Assert.Contains(
                "TRX is missing or empty",
                report.RootElement.GetProperty("PrimaryFailure").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrxFailureCannotBecomeACommandFailureWhenRecorderWriteFails()
    {
        var directory = CreateDirectory();
        try
        {
            var result = await RunFixtureAsync(directory);
            File.Delete(result.EvidencePath);
            Directory.CreateDirectory(result.EvidencePath);

            var exitCode = await Program.RunCommandAsync(
                ["run-project", "--evidence-directory", Path.GetDirectoryName(result.EvidencePath)!],
                (_, _) => CentralTestCommand.CompleteProjectResultAsync(
                    result, Path.Combine(directory, "missing.trx"), "fixture", "missing.trx"),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, exitCode);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.EvidencePath)!, "runner-command-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TopLevelFailureExitsSilentlyAfterPersistingPrimaryEvidence()
    {
        var directory = CreateDirectory();
        try
        {
            var evidenceDirectory = Path.Combine(directory, "evidence");
            var runtimeConfig = Path.Combine(
                AppContext.BaseDirectory, "DownKyi.Architecture.Tests.runtimeconfig.json");
            var runnerAssembly = typeof(FlightRecorderExecution).Assembly.Location;
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "exec", "--runtimeconfig", runtimeConfig, runnerAssembly,
                "run-project", "--repository-root", directory,
                "--unknown-option", "--evidence-directory", evidenceDirectory
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Equal(2, process.ExitCode);
            Assert.Equal(string.Empty, await stdout.ConfigureAwait(true));
            Assert.Equal(string.Empty, await stderr.ConfigureAwait(true));
            var evidencePath = Assert.Single(Directory.GetFiles(evidenceDirectory, "*.json"));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                evidencePath, TestContext.Current.CancellationToken));
            Assert.Equal("command_failed", report.RootElement.GetProperty("Outcome").GetString());
            Assert.Contains(
                "Unknown option",
                report.RootElement.GetProperty("PrimaryFailure").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TopLevelAggregatePreservesPrimaryBeforeSecondary()
    {
        var directory = CreateDirectory();
        try
        {
            var primary = new InvalidDataException("original test failure");
            var secondary = new IOException("inspector teardown failure");
            var exitCode = await Program.RunCommandAsync(
                ["run-project", "--evidence-directory", directory],
                (_, _) => Task.FromException<int>(new AggregateException(primary, secondary)),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, exitCode);
            var evidencePath = Assert.Single(Directory.GetFiles(directory, "*.json"));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                evidencePath, TestContext.Current.CancellationToken));
            Assert.Equal(
                primary.Message,
                report.RootElement.GetProperty("PrimaryFailure").GetString());
            Assert.Equal(
                secondary.Message,
                report.RootElement.GetProperty("SecondaryCleanupFailure").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<ProcessExecutionResult> RunFixtureAsync(string directory)
    {
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory, "DownKyi.Architecture.Tests.runtimeconfig.json");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", runtimeConfig,
            typeof(FlightRecorderExecution).Assembly.Location, "fixture-pass"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return FlightRecorderExecution.RunAsync(
            new ProcessExecutionRequest(
                "fixture", "passing", startInfo,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3),
                Path.Combine(directory, "evidence")),
            TestContext.Current.CancellationToken);
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-result-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
