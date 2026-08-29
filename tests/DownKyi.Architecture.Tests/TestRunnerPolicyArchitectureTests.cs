using System.Diagnostics;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class TestRunnerPolicyArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] RepositoryTestProjectPaths = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "tests"),
            "*.Tests.csproj",
            SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
        .Order(StringComparer.Ordinal)
        .ToArray();
    private static readonly string[] ExpectedRunnerDependencyInputs =
    [
        ".github/actions/test-project/action.yml",
        ".github/actions/test-solution/action.yml",
        "script/invoke-ci-test-action.ps1",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "docs/testing/test-runner-policy.json",
        "tests/CentralTestExecutionGuard.cs",
        "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj",
        "tools/DownKyi.CentralTestRunner/CentralTestAuthorization.cs",
        "tools/DownKyi.CentralTestRunner/CentralTestContracts.cs",
        "tools/DownKyi.CentralTestRunner/CentralTestExecutionValidator.cs",
        "tools/DownKyi.CentralTestRunner/CentralTestPolicy.cs",
        "tools/DownKyi.CentralTestRunner/CentralTestRunner.cs",
        "tools/DownKyi.ProcessSupervision/DownKyi.ProcessSupervision.csproj",
        "tools/DownKyi.ProcessSupervision/OwnedProcessLease.cs",
        "tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs",
        "tools/DownKyi.ProcessSupervision/SupervisorHost.cs"
    ];

    [Fact]
    public void EveryRepositoryTestProjectUsesTheCentralInProcessRunner()
    {
        using var policy = JsonDocument.Parse(Read("docs/testing/test-runner-policy.json"));
        var policyProjects = policy.RootElement
            .GetProperty("projects")
            .EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("project").GetString()!,
                entry => entry,
                StringComparer.Ordinal);
        Assert.Equal(RepositoryTestProjectPaths, policyProjects.Keys.Order(StringComparer.Ordinal));
        foreach (var project in policyProjects.Values)
        {
            Assert.Equal("xunit-in-process", project.GetProperty("runner").GetString());
            Assert.Equal("net10.0", project.GetProperty("targetFramework").GetString());
            Assert.Equal("none", project.GetProperty("parallel").GetString());
            Assert.False(string.IsNullOrWhiteSpace(project.GetProperty("reason").GetString()));
        }
    }

    [Fact]
    public void MsBuildProtocolGuardCannotBeAuthorizedByCallerProperties()
    {
        var project = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Architecture.Tests",
            "DownKyi.Architecture.Tests.csproj");

        var rejected = RunDotnet(
            "msbuild",
            project,
            "-t:EnforceDownKyiCentralTestRunner",
            "-p:IsTestProject=true");
        var forgedAuthorization = RunDotnet(
            "msbuild",
            project,
            "-t:EnforceDownKyiCentralTestRunner",
            "-p:IsTestProject=true",
            "-p:DownKyiCentralTestRunner=true");

        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains(
            "VSTest execution is disabled for repository test projects",
            rejected.Output,
            StringComparison.Ordinal);
        Assert.NotEqual(0, forgedAuthorization.ExitCode);
        Assert.Contains(
            "VSTest execution is disabled for repository test projects",
            forgedAuthorization.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryAnchorsTheRunnerProviderBeforeConsumingItsDependencyClosure()
    {
        var result = RunPowerShell(
            ". ./script/test-project-runner.ps1; " +
            "@(Get-DownKyiTestRunnerTrustInputs) | ConvertTo-Json -Compress");
        Assert.Equal(0, result.ExitCode);
        var inputs = JsonSerializer.Deserialize<string[]>(result.Output.Trim());
        Assert.Equal(ExpectedRunnerDependencyInputs, inputs);

        var recovery = Read(".github/workflows/release-v112-recovery.yml");
        var providerAnchor = recovery.IndexOf(
            "runner_trust_provider=script/test-project-runner.ps1",
            StringComparison.Ordinal);
        var providerInvocation = recovery.IndexOf(
            "Get-DownKyiTestRunnerTrustInputs",
            StringComparison.Ordinal);
        Assert.True(providerAnchor >= 0 && providerAnchor < providerInvocation);
    }

    [Fact]
    public void DirectAssemblyExecutionFailsTheRuntimeAuthorizationGuard()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-direct-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = CreateDirectArchitectureStartInfo(
                resultsDirectory,
                nameof(AgentEnvironmentArchitectureTests));
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
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void PowerShellBoundaryContainsNoTestProcessLifecycleOwner()
    {
        var projectWrapper = Read("script/test-project-runner.ps1");
        var solutionWrapper = Read("script/test-solution.ps1");
        var compiledRunner = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");

        Assert.Contains("CentralTestOrchestrator", projectWrapper, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiTestSolution", solutionWrapper, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "[Diagnostics.Process]::new",
                     "[System.Diagnostics.Process]::new",
                     "Wait-DownKyiOwnedProcessExit",
                     "Stop-DownKyiOwnedProcess",
                     ".Kill(",
                     ".WaitForExit(",
                     "Stopwatch]::StartNew"
                 })
        {
            Assert.DoesNotContain(forbidden, projectWrapper, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, solutionWrapper, StringComparison.Ordinal);
        }

        Assert.Contains("OwnedProcessLease.StartAsync", compiledRunner, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", compiledRunner, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", compiledRunner, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit", compiledRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralAuthorizationUsesAOneShotNamedEndpointInsteadOfNumericCapabilities()
    {
        AssertNamedAuthorizationTransport(
            Read("tools/DownKyi.CentralTestRunner/CentralTestAuthorization.cs"),
            Read("tests/CentralTestExecutionGuard.cs"));
    }

    [Fact]
    public void TestExecutionUsesOneCallerBudgetAcrossAuthorizationAndLease()
    {
        AssertSingleTestExecutionBudget(
            Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs"));
    }

    [Fact]
    public void NoBuildSkipsTheTargetButStillBootstrapsTheCompiledProvider()
    {
        var wrapper = Read("script/test-project-runner.ps1");

        Assert.Equal(2, CountOccurrences(wrapper, "-BuildIfMissing `"));
        Assert.DoesNotContain(
            "-BuildIfMissing:(-not $NoBuild)",
            wrapper,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(wrapper, "[bool]$NoBuild,"));
    }

    [Fact]
    public void MacTermResistanceFixtureHasOneTestOwnedProcessRoot()
    {
        var fixture = Read("tests/DownKyi.MacOS.Tests/MacBundleLayoutTests.cs");

        Assert.Contains(
            "#!/bin/bash\\ntrap '' TERM\\nwhile true; do :; done\\n",
            fixture,
            StringComparison.Ordinal);
        Assert.DoesNotContain("while true; do sleep 1; done", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void SupervisorHostTransportsButDoesNotAuthorizeRepositoryTests()
    {
        AssertSupervisorTransportOnly(Read("tools/DownKyi.ProcessSupervision/SupervisorHost.cs"));
    }

    [Fact]
    public void LinuxDelegationBootstrapCoversProjectAndSolutionModes()
    {
        var action = Read("script/invoke-ci-test-action.ps1");
        var review = Read("script/test-review-invariants.ps1");
        var delegationIndex = action.IndexOf(
            "Test-DownKyiDelegatedCgroupScopeRequired",
            StringComparison.Ordinal);
        var modeBranchIndex = action.IndexOf("if ($Mode -eq \"Solution\")", StringComparison.Ordinal);
        var reviewDelegationIndex = review.IndexOf(
            "Test-DownKyiDelegatedCgroupScopeRequired",
            StringComparison.Ordinal);
        var reviewRunnerIndex = review.IndexOf(
            ". (Join-Path $PSScriptRoot \"test-project-runner.ps1\")",
            StringComparison.Ordinal);

        Assert.True(delegationIndex >= 0 && delegationIndex < modeBranchIndex);
        Assert.Contains("-ArgumentList @(\"-Mode\", $Mode)", action, StringComparison.Ordinal);
        Assert.True(reviewDelegationIndex >= 0 && reviewDelegationIndex < reviewRunnerIndex);
        Assert.Contains(
            "ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters",
            review,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process", action + review, StringComparison.Ordinal);
        Assert.DoesNotContain("/proc/", action + review, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewInvariantMutationsUseRunnerNeutralClassLocators()
    {
        var runner = Read("script/test-review-invariants.ps1");
        var corpus = Read("docs/testing/review-invariant-corpus.json");

        Assert.Contains("-ClassNames @($proof.class)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("-Filter $proof.filter", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("FullyQualifiedName~", corpus, StringComparison.Ordinal);
    }

    internal static void AssertSupervisorTransportOnly(string source)
    {
        foreach (var forbidden in new[]
                 {
                     "CentralTestAuthorization",
                     "CentralTestExecutionGuard",
                     "test-runner-policy.json",
                     "DOWNKYI_CENTRAL_TEST_TOKEN",
                     "DOWNKYI_CENTRAL_TEST_ENDPOINT"
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    internal static void AssertNoRawLifecycleOwner(string source)
    {
        foreach (var forbidden in new[]
                 {
                     "Process.Start",
                     ".Kill(",
                     "WaitForExit",
                     "Stopwatch.StartNew",
                     "Process.GetProcessById"
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        Assert.Contains("OwnedProcessLease.StartAsync", source, StringComparison.Ordinal);
    }

    internal static void AssertNamedAuthorizationTransport(string issuer, string guard)
    {
        Assert.Contains("IpcEndpointName.Create", issuer, StringComparison.Ordinal);
        Assert.Contains("PipeOptions.CurrentUserOnly", issuer, StringComparison.Ordinal);
        Assert.Contains("NamedPipeServerStream", issuer, StringComparison.Ordinal);
        Assert.Contains("NamedPipeClientStream", guard, StringComparison.Ordinal);
        Assert.Contains("pipe.ReadByte() != -1", guard, StringComparison.Ordinal);
        Assert.Contains("LegacyPipeEnvironmentVariable", issuer, StringComparison.Ordinal);
        Assert.Contains("legacyPipeHandle", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("AnonymousPipe", issuer + guard, StringComparison.Ordinal);
        Assert.DoesNotContain("AnonymousPipeClientStream", issuer + guard, StringComparison.Ordinal);
    }

    internal static void AssertSingleTestExecutionBudget(string source)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var executionStart = source.IndexOf(
            "var budget = TransitionBudget.Start",
            StringComparison.Ordinal);
        var buildStart = source.IndexOf(
            "private static async Task BuildProjectAsync",
            StringComparison.Ordinal);
        Assert.True(executionStart >= 0 && buildStart > executionStart);
        var execution = source[executionStart..buildStart];

        Assert.Equal(1, CountOccurrences(execution, "TransitionBudget.Start"));
        Assert.Contains("authorization.CompleteAsync(budget", execution, StringComparison.Ordinal);
        Assert.Contains("OwnedProcessLease.StartAsync", execution, StringComparison.Ordinal);
        Assert.Contains("launchSpec,\n                        budget", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", execution, StringComparison.Ordinal);
    }

    internal static void AssertLinuxDelegationHasNoEnumerationFallback(string source)
    {
        Assert.DoesNotContain("Get-Process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcesses", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Kill(entireProcessTree", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    internal static ProcessStartInfo CreateDirectArchitectureStartInfo(
        string resultsDirectory,
        string className)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(TestRunnerPolicyArchitectureTests).Assembly.Location);
        startInfo.ArgumentList.Add("-noLogo");
        startInfo.ArgumentList.Add("-noColor");
        startInfo.ArgumentList.Add("-noAutoReporters");
        startInfo.ArgumentList.Add("-reporter");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-parallel");
        startInfo.ArgumentList.Add("none");
        startInfo.ArgumentList.Add("-class");
        startInfo.ArgumentList.Add($"DownKyi.Architecture.Tests.{className}");
        startInfo.ArgumentList.Add("-trx");
        startInfo.ArgumentList.Add(Path.Combine(resultsDirectory, "direct.trx"));
        return startInfo;
    }

    private static BoundedProcessResult RunDotnet(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return BoundedProcessRunner.Run(
            startInfo,
            TestContext.Current.CancellationToken);
    }

    private static BoundedProcessResult RunPowerShell(string command)
    {
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
        startInfo.ArgumentList.Add(command);
        return BoundedProcessRunner.Run(
            startInfo,
            TestContext.Current.CancellationToken);
    }

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
