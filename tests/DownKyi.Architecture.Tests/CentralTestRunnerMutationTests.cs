using System.Collections.Immutable;
using System.Diagnostics;
using DownKyi.CentralTestRunner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerMutationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly ImmutableArray<MetadataReference> PlatformReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

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
    public void TestAssemblyGuardCannotBeDisabledByCallerOrRemovedByMutation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-guard-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primaryFailure = Record.Exception(() =>
        {
            var startInfo = CentralTestAuthorizationTests.CreateDirectStartInfo(
                Path.Combine(directory, "direct.trx"));
            startInfo.Environment["DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS"] = "1";
            var productionResult = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, productionResult.ExitCode);
            Assert.Contains(
                "must execute through the central in-process test runner",
                productionResult.Output,
                StringComparison.OrdinalIgnoreCase);

            if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS_PROOF"))
            {
                var guardSource = Read("tests/CentralTestExecutionGuard.cs")
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
                const string initializerBoundary =
                    "internal static void RequireInProcessTestHost()\n" +
                    "    {\n" +
                    "        ConsumeLifecycleMarkerOwnership();";
                Assert.Contains(initializerBoundary, guardSource, StringComparison.Ordinal);
                guardSource = guardSource.Replace(
                    initializerBoundary,
                    "internal static void RequireInProcessTestHost()\n" +
                    "    {\n" +
                    "        return;",
                    StringComparison.Ordinal);

                var fixtureStartInfo = CompileGuardFixture(directory, guardSource);
                fixtureStartInfo.Environment[
                    "DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS"] = "1";
                var fixtureResult = BoundedProcessRunner.Run(
                    fixtureStartInfo,
                    TestContext.Current.CancellationToken);

                Assert.NotEqual(0, fixtureResult.ExitCode);
                Assert.Contains(
                    "must execute through the central in-process test runner",
                    fixtureResult.Output,
                StringComparison.OrdinalIgnoreCase);
            }
        });

        var cleanupFailure = Record.Exception(
            () => Directory.Delete(directory, recursive: true));

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(primaryFailure, cleanupFailure);
        }
        if (primaryFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(primaryFailure)
                .Throw();
        }
        if (cleanupFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(cleanupFailure)
                .Throw();
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
                     Read("script/test-project.ps1") +
                     Read("script/test-solution.ps1") +
                     Read("script/test-review-invariants.ps1") +
                     Read("script/delegated-cgroup-scope.ps1") +
                     Read("tools/DownKyi.ProcessSupervision/LinuxCgroupContainmentLease.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_LINUX_FALLBACK"))
        {
            source += " Get-Process";
        }

        TestRunnerPolicyArchitectureTests.AssertLinuxDelegationHasNoEnumerationFallback(source);
    }

    [Fact]
    public void AuthorizationCompletionMustObserveAuthoritativeTargetExit()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs") +
                     Read("tools/DownKyi.CentralTestRunner/CentralTestAuthorization.cs") +
                     Read("script/test-project-runner.ps1") +
                     Read("script/test-assembly-lifecycle.ps1");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_TARGET_EXIT_AUTHORITY"))
        {
            source = source.Replace("lease.TargetExitedToken", "CancellationToken.None", StringComparison.Ordinal);
        }

        Assert.Contains("lease.TargetExitedToken", source, StringComparison.Ordinal);
        Assert.Contains("targetExitedToken.IsCancellationRequested", source, StringComparison.Ordinal);
        Assert.Contains("-TargetExitedToken $lease.TargetExitedToken", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupAggregationCannotPrecedeThePrimaryFailure()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_PRIMARY_CLEANUP_ORDER"))
        {
            source = source.Replace(
                "new[] { primaryFailure }.Concat(cleanupFailures)",
                "cleanupFailures.Concat(new[] { primaryFailure })",
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "new[] { primaryFailure }.Concat(cleanupFailures)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ExceptionDispatchInfo.Capture(primaryFailure).Throw()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerDiagnosticsCannotRenderCanonicalCheckoutPaths()
    {
        var source = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_ABSOLUTE_DIAGNOSTICS"))
        {
            source += " Testing {project.FullName}";
        }

        Assert.DoesNotContain("Testing {project.FullName}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test project failed: {project}", source, StringComparison.Ordinal);
        Assert.Contains("FormatRepositoryPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectProjectEntryMustAcquireDelegatedScope()
    {
        var source = Read("script/test-project.ps1");
        if (MutationIsActive("DOWNKYI_TEST_MUTATE_CENTRAL_DIRECT_LINUX_SCOPE"))
        {
            source = source.Replace(
                "if (Test-DownKyiDelegatedCgroupScopeRequired)",
                "if ($false)",
                StringComparison.Ordinal);
        }

        Assert.Contains("if (Test-DownKyiDelegatedCgroupScopeRequired)", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiDelegatedCgroupScope", source, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("Test-DownKyiDelegatedCgroupScopeRequired", StringComparison.Ordinal) <
            source.IndexOf("test-project-runner.ps1", StringComparison.Ordinal));
    }

    private static ProcessStartInfo CompileGuardFixture(string directory, string guardSource)
    {
        var assemblyName = $"DownKyi.GuardMutation.{Guid.NewGuid():N}";
        var assemblyPath = Path.Combine(directory, assemblyName + ".dll");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [
                CSharpSyntaxTree.ParseText(
                    "global using System;\n" +
                    "global using System.IO;\n" +
                    guardSource,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    "CentralTestExecutionGuard.cs"),
                CSharpSyntaxTree.ParseText(
                    "internal static class Program { public static void Main() { " +
                    "System.Console.WriteLine(\"guard fixture executed\"); } }",
                    new CSharpParseOptions(LanguageVersion.Latest),
                    "Program.cs")
            ],
            PlatformReferences,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release));
        using (var output = File.Create(assemblyPath))
        {
            var emitted = compilation.Emit(output);
            Assert.True(
                emitted.Success,
                string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        File.WriteAllText(
            Path.Combine(directory, assemblyName + ".runtimeconfig.json"),
            $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{Environment.Version.Major}}.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{Environment.Version.Major}}.0.0"
                }
              }
            }
            """);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        return startInfo;
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
