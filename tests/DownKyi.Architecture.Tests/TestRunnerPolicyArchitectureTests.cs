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
        "tests/CentralTestExecutionGuard.cs"
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

        var reason = policyProjects["tests/DownKyi.Tests/DownKyi.Tests.csproj"]
            .GetProperty("reason")
            .GetString();
        Assert.Contains("xunit/xunit#3576", reason, StringComparison.Ordinal);
        Assert.Contains("assembly-info stdout protocol corruption", reason, StringComparison.Ordinal);
        Assert.Contains("lifecycle", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separately verified", reason, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Get-DownKyiTestRunnerTrustInputs", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void VstestRunnerMutationFailsTheRuntimeExecutionOwnershipGuard()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-vstest-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
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
            startInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                function Invoke-DownKyiAuthorizedTestAssembly {
                    param(
                        [string]$RepositoryRoot,
                        [string[]]$Arguments,
                        [object]$Authorization)
                    $assembly = $Arguments[0]
                    $classIndex = [Array]::IndexOf($Arguments, '-class')
                    $trxIndex = [Array]::IndexOf($Arguments, '-trx')
                    $className = $Arguments[$classIndex + 1]
                    $trxPath = $Arguments[$trxIndex + 1]
                    & dotnet vstest $assembly `
                        "--Tests:$className" `
                        "--logger:trx;LogFileName=$([IO.Path]::GetFileName($trxPath))" `
                        "--ResultsDirectory:$([IO.Path]::GetDirectoryName($trxPath))" |
                        Out-Host
                    return $LASTEXITCODE
                }
                try {
                    Invoke-DownKyiTestProject `
                        -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                        -ProjectPath $env:DOWNKYI_ARCHITECTURE_PROJECT `
                        -Configuration Release `
                        -NoRestore `
                        -NoBuild `
                        -ResultsDirectory $env:DOWNKYI_MUTATION_RESULTS `
                        -TrxName mutation.trx `
                        -ClassNames DownKyi.Architecture.Tests.TestRunnerPolicyArchitectureTests.EveryRepositoryTestProjectUsesTheCentralInProcessRunner
                    exit 0
                }
                catch {
                    Write-Error $_
                    exit 73
                }
                """);
            startInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_ARCHITECTURE_PROJECT"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "DownKyi.Architecture.Tests.csproj");
            startInfo.Environment["DOWNKYI_MUTATION_RESULTS"] = resultsDirectory;

            var mutation = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, mutation.ExitCode);
            Assert.Contains("expected test report", mutation.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void AuthorizedSubsetMutationFailsTheCompleteInvocationContract()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-subset-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
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
            startInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                function Invoke-DownKyiAuthorizedTestAssembly {
                    param(
                        [string]$RepositoryRoot,
                        [string[]]$Arguments,
                        [object]$Authorization)
                    $trxIndex = [Array]::IndexOf($Arguments, '-trx')
                    $subsetArguments = @(
                        $Arguments[0],
                        '-noLogo',
                        '-noColor',
                        '-noAutoReporters',
                        '-reporter', 'quiet',
                        '-parallel', 'none',
                        '-class', 'DownKyi.Architecture.Tests.AgentEnvironmentArchitectureTests',
                        '-trx', $Arguments[$trxIndex + 1]
                    )
                    $replacementContract = [Tuple]::Create(
                        [Collections.ObjectModel.ReadOnlyCollection[string]]::new(
                            [string[]]$subsetArguments),
                        [Convert]::ToBase64String(
                            (Get-DownKyiTestInvocationHash -Arguments $subsetArguments)),
                        $Authorization.Item1.Item3)
                    $rebindRejected = $false
                    try {
                        $Authorization.Item1 = $replacementContract
                    }
                    catch {
                        $rebindRejected = $true
                    }
                    if (-not $rebindRejected) {
                        throw 'The immutable invocation contract was rebound.'
                    }
                    $mutatedStartInfo = [Diagnostics.ProcessStartInfo]::new()
                    $mutatedStartInfo.FileName = 'dotnet'
                    $mutatedStartInfo.WorkingDirectory = $RepositoryRoot
                    $mutatedStartInfo.UseShellExecute = $false
                    foreach ($argument in $subsetArguments) {
                        $mutatedStartInfo.ArgumentList.Add($argument)
                    }
                    Set-DownKyiTestProcessAuthorization `
                        -Authorization $Authorization `
                        -StartInfo $mutatedStartInfo
                    throw 'The subset mutation unexpectedly received authorization.'
                }
                try {
                    Invoke-DownKyiTestProject `
                        -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                        -ProjectPath $env:DOWNKYI_ARCHITECTURE_PROJECT `
                        -Configuration Release `
                        -NoRestore `
                        -NoBuild `
                        -ResultsDirectory $env:DOWNKYI_MUTATION_RESULTS `
                        -TrxName mutation.trx
                    exit 0
                }
                catch {
                    Write-Error $_
                    exit 73
                }
                """);
            startInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_ARCHITECTURE_PROJECT"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "DownKyi.Architecture.Tests.csproj");
            startInfo.Environment["DOWNKYI_MUTATION_RESULTS"] = resultsDirectory;

            var mutation = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, mutation.ExitCode);
            Assert.Contains(
                "complete invocation contract",
                mutation.Output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void ChildGuardRejectsAMutatedInvocationContractHash()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-contract-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
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
            startInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                function Get-DownKyiTestInvocationHash {
                    param([string[]]$Arguments)
                    return [byte[]]::new(32)
                }
                $arguments = @(
                    $env:DOWNKYI_ARCHITECTURE_ASSEMBLY,
                    '-noLogo',
                    '-noColor',
                    '-noAutoReporters',
                    '-reporter', 'quiet',
                    '-parallel', 'none',
                    '-class', 'DownKyi.Architecture.Tests.AgentEnvironmentArchitectureTests',
                    '-trx', $env:DOWNKYI_MUTATION_TRX
                )
                $authorization = New-DownKyiTestProcessAuthorization `
                    -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                    -Arguments $arguments
                $exitCode = Invoke-DownKyiAuthorizedTestAssembly `
                    -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                    -Arguments $arguments `
                    -Authorization $authorization
                if ($exitCode -eq 0) {
                    throw 'The child accepted a mutated invocation contract hash.'
                }
                Write-Output 'Child rejected mutated invocation hash.'
                """);
            startInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_ARCHITECTURE_ASSEMBLY"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "bin",
                "Release",
                "net10.0",
                "DownKyi.Architecture.Tests.dll");
            startInfo.Environment["DOWNKYI_MUTATION_TRX"] =
                Path.Combine(directory, "mutation.trx");

            var mutation = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, mutation.ExitCode);
            Assert.Contains(
                "Child rejected mutated invocation hash.",
                mutation.Output,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AuthorizationSetupFailureTerminatesTheStartedChildProcess()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-setup-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
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
            startInfo.ArgumentList.Add("""
                . $env:DOWNKYI_TEST_RUNNER
                $completeAuthorization = ${function:Complete-DownKyiTestProcessAuthorization}
                function Complete-DownKyiTestProcessAuthorization {
                    param([object]$Authorization)
                    & $completeAuthorization -Authorization $Authorization
                    Set-Content `
                        -LiteralPath $env:DOWNKYI_CHILD_PID `
                        -Value $Authorization.Item2.ChildProcessId
                    throw 'Injected authorization setup failure.'
                }
                $arguments = @(
                    $env:DOWNKYI_ARCHITECTURE_ASSEMBLY,
                    '-noLogo',
                    '-noColor',
                    '-noAutoReporters',
                    '-reporter', 'quiet',
                    '-parallel', 'none',
                    '-class', 'DownKyi.Architecture.Tests.CiTestActionBehaviorTests',
                    '-trx', $env:DOWNKYI_MUTATION_TRX
                )
                $authorization = New-DownKyiTestProcessAuthorization `
                    -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                    -Arguments $arguments
                try {
                    Invoke-DownKyiAuthorizedTestAssembly `
                        -RepositoryRoot $env:DOWNKYI_REPOSITORY_ROOT `
                        -Arguments $arguments `
                        -Authorization $authorization
                    throw 'The injected setup failure did not propagate.'
                }
                catch {
                    $childId = [int](Get-Content -LiteralPath $env:DOWNKYI_CHILD_PID -Raw)
                    if ($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
                        throw "Authorized child $childId survived its owner failure."
                    }
                    Write-Output 'Started child was terminated.'
                }
                """);
            startInfo.Environment["DOWNKYI_TEST_RUNNER"] =
                Path.Combine(RepositoryRoot, "script", "test-project-runner.ps1");
            startInfo.Environment["DOWNKYI_REPOSITORY_ROOT"] = RepositoryRoot;
            startInfo.Environment["DOWNKYI_ARCHITECTURE_ASSEMBLY"] = Path.Combine(
                RepositoryRoot,
                "tests",
                "DownKyi.Architecture.Tests",
                "bin",
                "Release",
                "net10.0",
                "DownKyi.Architecture.Tests.dll");
            startInfo.Environment["DOWNKYI_MUTATION_TRX"] =
                Path.Combine(directory, "mutation.trx");
            startInfo.Environment["DOWNKYI_CHILD_PID"] = Path.Combine(directory, "child.pid");

            var mutation = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, mutation.ExitCode);
            Assert.Contains("Started child was terminated.", mutation.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReviewInvariantMutationsUseRunnerNeutralClassLocators()
    {
        var runner = Read("script/test-review-invariants.ps1");
        var corpus = Read("docs/testing/review-invariant-corpus.json");

        Assert.Contains("-ClassNames @($proof.class)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("-Filter $proof.filter", runner, StringComparison.Ordinal);
        Assert.Contains(
            "\"class\": \"DownKyi.Tests.DownloadArtifactStageTests\"",
            corpus,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FullyQualifiedName~", corpus, StringComparison.Ordinal);
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

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
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
