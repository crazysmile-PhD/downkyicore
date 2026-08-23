using System.Diagnostics;

namespace DownKyi.Architecture.Tests;

public sealed class TestExecutionValidatorBehaviorTests
{
    private const string ExpectedClass = "DownKyi.Tests.Aria2TlsIntegrationTests";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("missing-report")]
    [InlineData("missing-counters")]
    [InlineData("zero-executed")]
    [InlineData("malformed-counters")]
    [InlineData("malformed-report")]
    [InlineData("multiple-reports")]
    [InlineData("other-class-only")]
    [InlineData("expected-class-not-executed")]
    [InlineData("runner-failure")]
    public void ValidatorRejectsFalseGreenReports(string scenario)
    {
        var result = InvokeValidator(scenario);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void ValidatorAcceptsRunnerSuccessWithExecutedExpectedClassResult()
    {
        var result = InvokeValidator("valid");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ExecutedExpected", result.Output, StringComparison.Ordinal);
    }

    private static ProcessResult InvokeValidator(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-trx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "result.trx");
        try
        {
            var report = CreateReport(scenario);
            if (report != null)
            {
                File.WriteAllText(reportPath, report);
            }
            if (scenario == "multiple-reports")
            {
                File.WriteAllText(Path.Combine(directory, "unexpected.trx"), report!);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    WorkingDirectory = RepositoryRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                Assert-DownKyiExpectedTestExecution `
                  -RunnerExitCode ([int]$env:DOWNKYI_RUNNER_EXIT) `
                  -TrxPath $env:DOWNKYI_TRX_PATH `
                  -ExpectedClassNames @($env:DOWNKYI_EXPECTED_CLASS)
                """);
            process.StartInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            process.StartInfo.Environment["DOWNKYI_TRX_PATH"] = reportPath;
            process.StartInfo.Environment["DOWNKYI_EXPECTED_CLASS"] = ExpectedClass;
            process.StartInfo.Environment["DOWNKYI_RUNNER_EXIT"] =
                scenario == "runner-failure" ? "1" : "0";

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new ProcessResult(
                process.ExitCode,
                standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? CreateReport(string scenario)
    {
        return scenario switch
        {
            "missing-report" => null,
            "malformed-report" => "<TestRun>",
            "multiple-reports" => CreateTrx(ExpectedClass, true, "1", "Passed"),
            "missing-counters" => CreateTrx(ExpectedClass, includeCounters: false, "1", "Passed"),
            "zero-executed" => CreateTrx(ExpectedClass, includeCounters: true, "0", "NotExecuted"),
            "malformed-counters" => CreateTrx(ExpectedClass, true, "invalid", "Passed"),
            "other-class-only" => CreateTrx("DownKyi.Tests.UnrelatedTests", true, "1", "Passed"),
            "expected-class-not-executed" => CreateTrx(ExpectedClass, true, "1", "NotExecuted"),
            "runner-failure" => CreateTrx(ExpectedClass, true, "1", "Passed"),
            "valid" => CreateTrx(ExpectedClass, true, "1", "Passed"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static string CreateTrx(
        string className,
        bool includeCounters,
        string executed,
        string outcome)
    {
        var counters = includeCounters
            ? $"<Counters total=\"{executed}\" executed=\"{executed}\" passed=\"{executed}\" failed=\"0\" />"
            : string.Empty;
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="test-1" executionId="execution-1" testName="Probe" outcome="{{outcome}}" />
              </Results>
              <TestDefinitions>
                <UnitTest id="test-1" name="Probe">
                  <TestMethod className="{{className}}" name="Probe" />
                </UnitTest>
              </TestDefinitions>
              <ResultSummary outcome="Completed">{{counters}}</ResultSummary>
            </TestRun>
            """;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
