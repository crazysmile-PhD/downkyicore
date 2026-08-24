using System.Diagnostics;

namespace DownKyi.Architecture.Tests;

public sealed class TestExecutionValidatorBehaviorTests
{
    private const string ExpectedClass = "DownKyi.Tests.Aria2TlsIntegrationTests";
    private const string SecondExpectedClass = "DownKyi.Tests.Aria2TlsPolicyTests";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("missing-report")]
    [InlineData("missing-counters")]
    [InlineData("zero-executed")]
    [InlineData("malformed-counters")]
    [InlineData("contradictory-passed-counter")]
    [InlineData("contradictory-total-counter")]
    [InlineData("malformed-report")]
    [InlineData("multiple-reports")]
    [InlineData("other-class-only")]
    [InlineData("expected-class-not-executed")]
    [InlineData("expected-class-failed-with-success-exit")]
    [InlineData("second-expected-class-not-executed")]
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

    [Fact]
    public void ValidatorAcceptsOnlyWhenEveryExpectedClassExecutedAndPassed()
    {
        var result = InvokeValidator("valid-multiple-classes");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ExecutedExpectedClasses", result.Output, StringComparison.Ordinal);
    }

    private static BoundedProcessResult InvokeValidator(string scenario)
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

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                Assert-DownKyiExpectedTestExecution `
                  -RunnerExitCode ([int]$env:DOWNKYI_RUNNER_EXIT) `
                  -TrxPath $env:DOWNKYI_TRX_PATH `
                  -ExpectedClassNames @($env:DOWNKYI_EXPECTED_CLASSES.Split(';'))
                """);
            startInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_TRX_PATH"] = reportPath;
            startInfo.Environment["DOWNKYI_EXPECTED_CLASSES"] =
                scenario is "second-expected-class-not-executed" or "valid-multiple-classes"
                    ? $"{ExpectedClass};{SecondExpectedClass}"
                    : ExpectedClass;
            startInfo.Environment["DOWNKYI_RUNNER_EXIT"] =
                scenario == "runner-failure" ? "1" : "0";

            return BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);
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
            "zero-executed" => CreateTrx(
                ExpectedClass,
                includeCounters: true,
                executed: "0",
                outcome: "NotExecuted",
                total: "1",
                passed: "0"),
            "malformed-counters" => CreateTrx(ExpectedClass, true, "invalid", "Passed"),
            "contradictory-passed-counter" => CreateTrx(
                ExpectedClass,
                true,
                "1",
                "Passed",
                passed: "0"),
            "contradictory-total-counter" => CreateTrx(
                ExpectedClass,
                true,
                "1",
                "Passed",
                total: "2"),
            "other-class-only" => CreateTrx("DownKyi.Tests.UnrelatedTests", true, "1", "Passed"),
            "expected-class-not-executed" => CreateTrx(
                ExpectedClass,
                true,
                "0",
                "NotExecuted",
                total: "1",
                passed: "0"),
            "expected-class-failed-with-success-exit" => CreateTrx(
                ExpectedClass,
                true,
                "1",
                "Failed",
                passed: "0",
                failed: "1"),
            "runner-failure" => CreateTrx(ExpectedClass, true, "1", "Passed"),
            "valid" => CreateTrx(ExpectedClass, true, "1", "Passed"),
            "second-expected-class-not-executed" => CreateTwoClassTrx(
                secondClassExecuted: false),
            "valid-multiple-classes" => CreateTwoClassTrx(secondClassExecuted: true),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static string CreateTwoClassTrx(bool secondClassExecuted)
    {
        var secondOutcome = secondClassExecuted ? "Passed" : "NotExecuted";
        var executed = secondClassExecuted ? "2" : "1";
        var passed = executed;
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="test-1" executionId="execution-1" testName="ProbeOne" outcome="Passed" />
                <UnitTestResult testId="test-2" executionId="execution-2" testName="ProbeTwo" outcome="{{secondOutcome}}" />
              </Results>
              <TestDefinitions>
                <UnitTest id="test-1" name="ProbeOne">
                  <TestMethod className="{{ExpectedClass}}" name="ProbeOne" />
                </UnitTest>
                <UnitTest id="test-2" name="ProbeTwo">
                  <TestMethod className="{{SecondExpectedClass}}" name="ProbeTwo" />
                </UnitTest>
              </TestDefinitions>
              <ResultSummary outcome="Completed">
                <Counters total="2" executed="{{executed}}" passed="{{passed}}" failed="0" />
              </ResultSummary>
            </TestRun>
            """;
    }

    private static string CreateTrx(
        string className,
        bool includeCounters,
        string executed,
        string outcome,
        string? total = null,
        string? passed = null,
        string failed = "0")
    {
        total ??= executed;
        passed ??= executed;
        var counters = includeCounters
            ? $"<Counters total=\"{total}\" executed=\"{executed}\" passed=\"{passed}\" failed=\"{failed}\" />"
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
}
