using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerMutationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void RawProcessStartCannotReturnToTheCentralRunner()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_RAW_START"))
        {
            source += " Process.Start";
        }

        TestRunnerPolicyArchitectureTests.AssertNoRawLifecycleOwner(source);
    }

    [Fact]
    public void NumericHandleOrFileDescriptorCannotBecomeAuthorizationTransport()
    {
        var issuer = Read("tools/DownKyi.CentralTestRunner/CentralTestAuthorization.cs");
        var guard = Read("tests/CentralTestExecutionGuard.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_NUMERIC_CAPABILITY"))
        {
            issuer += " AnonymousPipeClientStream DOWNKYI_CENTRAL_TEST_PIPE";
        }

        TestRunnerPolicyArchitectureTests.AssertNamedAuthorizationTransport(issuer, guard);
    }

    [Fact]
    public void SupervisorHostCannotBecomeTestAuthorizationAuthority()
    {
        var source = Read("tools/DownKyi.ProcessSupervision/SupervisorHost.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_SUPERVISOR_AUTHORITY"))
        {
            source += " CentralTestAuthorization";
        }

        TestRunnerPolicyArchitectureTests.AssertSupervisorTransportOnly(source);
    }

    [Fact]
    public void TestAssemblyGuardCannotBeBypassed()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-guard-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var startInfo = CentralTestAuthorizationTests.CreateDirectStartInfo(
                Path.Combine(directory, "direct.trx"));
            if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS_PROOF"))
            {
                startInfo.Environment["DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS"] = "1";
            }

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "must execute through the central in-process test runner",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OneShotAuthorizationCannotBeReplayed()
    {
        var mutation = MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_AUTHORIZATION_REPLAY")
            ? CentralTestAuthorizationMutation.Replay
            : CentralTestAuthorizationMutation.None;
        using var result = await CentralTestAuthorizationTests.RunAuthorizedChildAsync(mutation)
            .ConfigureAwait(true);

        Assert.Equal(0, result.Outcome.ExitCode);
        Assert.True(result.Outcome.TreeQuiescent);
    }

    [Fact]
    public void TestExecutionCannotCreateAFreshLifecycleDeadline()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_FRESH_DEADLINE"))
        {
            var buildBoundary = source.IndexOf(
                "private static async Task BuildProjectAsync",
                StringComparison.Ordinal);
            source = source.Insert(buildBoundary, " TransitionBudget.Start");
        }

        TestRunnerPolicyArchitectureTests.AssertSingleTestExecutionBudget(source);
    }

    [Fact]
    public void CentralRunnerCannotOwnPrivateTerminateOrReapLogic()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_PRIVATE_CLEANUP"))
        {
            source += " .Kill(";
        }

        TestRunnerPolicyArchitectureTests.AssertNoRawLifecycleOwner(source);
    }

    [Fact]
    public void ProcessExitZeroCannotOverrideFailedTrxSemantics()
    {
        var mutation = MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_EXIT_CODE_TRX")
            ? CentralTestValidatorMutation.TreatProcessExitZeroAsPass
            : CentralTestValidatorMutation.None;
        WithSyntheticTrx(
            executed: 2,
            passed: 1,
            failed: 1,
            results:
            [
                ("passing", "Passed"),
                ("failing", "Failed")
            ],
            trxPath => Assert.Throws<InvalidOperationException>(() =>
                CentralTestExecutionValidator.ValidateExpectedExecutionForTesting(
                    0,
                    trxPath,
                    ["DownKyi.Architecture.Tests.SyntheticFixture"],
                    mutation)));
    }

    [Fact]
    public void ZeroExecutedTestsRemainFailClosed()
    {
        var mutation = MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_ZERO_TEST")
            ? CentralTestValidatorMutation.AcceptZeroExecuted
            : CentralTestValidatorMutation.None;
        WithSyntheticTrx(
            executed: 0,
            passed: 0,
            failed: 0,
            results: [("not-executed", "NotExecuted")],
            trxPath => Assert.Throws<InvalidDataException>(() =>
                CentralTestExecutionValidator.ValidateReportForTesting(
                    trxPath,
                    expectedClassNames: null,
                    requireUniqueReport: true,
                    mutation)));
    }

    [Fact]
    public void LinuxDelegationCannotFallBackToProcessEnumeration()
    {
        var source = Read("script/invoke-ci-test-action.ps1") +
                     Read("script/delegated-cgroup-scope.ps1") +
                     Read("tools/DownKyi.ProcessSupervision/LinuxCgroupContainmentLease.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_LINUX_FALLBACK"))
        {
            source += " Get-Process";
        }

        TestRunnerPolicyArchitectureTests.AssertLinuxDelegationHasNoEnumerationFallback(source);
    }

    private static void WithSyntheticTrx(
        int executed,
        int passed,
        int failed,
        IReadOnlyList<(string Name, string Outcome)> results,
        Action<string> assertion)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-trx-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var trxPath = Path.Combine(directory, "synthetic.trx");
        try
        {
            var definitions = string.Join(
                string.Empty,
                results.Select(result =>
                    $"<UnitTest id='{result.Name}' name='{result.Name}'>" +
                    "<TestMethod className='DownKyi.Architecture.Tests.SyntheticFixture' " +
                    $"name='{result.Name}' /></UnitTest>"));
            var executions = string.Join(
                string.Empty,
                results.Select(result =>
                    $"<UnitTestResult testId='{result.Name}' outcome='{result.Outcome}' />"));
            var document =
                "<TestRun><TestDefinitions>" + definitions +
                "</TestDefinitions><Results>" + executions +
                "</Results><ResultSummary><Counters " +
                $"total='{results.Count}' executed='{executed}' passed='{passed}' failed='{failed}' />" +
                "</ResultSummary></TestRun>";
            File.WriteAllText(trxPath, document);
            assertion(trxPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool MutationIsActive(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "1",
            StringComparison.Ordinal);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
