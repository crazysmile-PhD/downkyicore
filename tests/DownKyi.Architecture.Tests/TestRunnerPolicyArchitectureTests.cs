using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private static readonly string[] RepositoryTestAssemblyNames = RepositoryTestProjectPaths
        .Select(path => Path.GetFileNameWithoutExtension(path)
            ?? throw new InvalidOperationException($"Test project has no assembly name: {path}"))
        .ToArray();
    private static readonly string[] ExpectedRunnerTrustInputs =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "docs/testing/test-runner-policy.json",
        "script/test-project-runner.ps1",
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
    public void FormalWorkflowsDelegateRepositoryTestsToTheSharedRunner()
    {
        var workflowPaths = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, ".github", "workflows"),
                "*.y*ml",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var violations = workflowPaths
            .SelectMany(workflowPath => ExtractWorkflowRunScripts(File.ReadAllText(workflowPath))
                .Where(ContainsDirectTestEntrypoint)
                .Select(script => $"{Path.GetFileName(workflowPath)}: {script}"))
            .ToArray();
        Assert.True(
            violations.Length == 0,
            $"Formal workflows contain direct test execution capabilities:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");

        AssertWorkflowUsesSharedRunner(
            ".github/workflows/quality.yml",
            ". ./script/test-project-runner.ps1");
        AssertWorkflowUsesSharedRunner(
            ".github/workflows/build.yml",
            ". ./script/test-project-runner.ps1");
        AssertWorkflowUsesSharedRunner(
            ".github/workflows/release-v112-recovery.yml",
            ". ./tooling/script/test-project-runner.ps1");
    }

    [Theory]
    [InlineData("dotnet test $unknownTarget")]
    [InlineData("dotnet $unknownVerb $unknownTarget")]
    [InlineData("dotnet vstest $unknownAssembly")]
    [InlineData("dotnet ./tools/xunit.v3.runner.console.dll $unknownAssembly")]
    [InlineData("vstest.console.exe $unknownAssembly")]
    [InlineData("exec dotnet test $unknownTarget -p:DownKyiCentralTestRunner=true")]
    [InlineData("sudo dotnet vstest $unknownAssembly")]
    [InlineData("dotnet $unknownAssembly")]
    [InlineData("dotnet ./tests/DownKyi.Tests/bin/Release/net10.0/DownKyi.Tests.dll")]
    [InlineData("./tests/DownKyi.Tests/bin/Release/net10.0/DownKyi.Tests")]
    [InlineData("dotnet run --project ./tests/DownKyi.Tests/DownKyi.Tests.csproj")]
    public void WorkflowTestCapabilityIsRejectedWithoutInferringItsTarget(string runScript)
    {
        Assert.True(ContainsDirectTestEntrypoint(runScript));
    }

    [Fact]
    public void SharedRunnerInvocationDoesNotGrantWorkflowDirectExecutionCapability()
    {
        const string runScript = """
            . ./script/test-project-runner.ps1
            Invoke-DownKyiTestProject -ProjectPath $unknownTarget
            """;

        Assert.False(ContainsDirectTestEntrypoint(runScript));
    }

    [Theory]
    [InlineData("dotnet restore ./DownKyi.sln")]
    [InlineData("dotnet build ./DownKyi.sln -c Release")]
    [InlineData("dotnet --info")]
    [InlineData("dotnet run --project ./benchmarks/DownKyi.SystemBenchmarks/DownKyi.SystemBenchmarks.csproj")]
    public void StaticallyProvenNonTestDotnetCapabilitiesRemainAllowed(string runScript)
    {
        Assert.False(ContainsDirectTestEntrypoint(runScript));
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
    public void RunnerDeclaresItsCompleteReleaseTrustInputSet()
    {
        var result = RunPowerShell(
            ". ./script/test-project-runner.ps1; " +
            "@(Get-DownKyiTestRunnerTrustInputs) | ConvertTo-Json -Compress");
        Assert.Equal(0, result.ExitCode);
        var inputs = JsonSerializer.Deserialize<string[]>(result.Output.Trim());
        Assert.Equal(ExpectedRunnerTrustInputs, inputs);

        var recovery = Read(".github/workflows/release-v112-recovery.yml");
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
                    param([string]$RepositoryRoot, [string[]]$Arguments)
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
                        -ClassNames DownKyi.Architecture.Tests.TestRunnerPolicyArchitectureTests.SharedRunnerInvocationDoesNotGrantWorkflowDirectExecutionCapability
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
    public void Aria2TlsWorkflowUsesExecutableExpectedClassValidation()
    {
        var workflow = Read(".github/workflows/quality.yml");
        var stepStart = workflow.IndexOf(
            "      - name: Verify packaged aria2 TLS behavior",
            StringComparison.Ordinal);
        var stepEnd = workflow.IndexOf(
            "      - name: Upload sanitized aria2 TLS report",
            stepStart,
            StringComparison.Ordinal);
        Assert.True(stepStart >= 0 && stepEnd > stepStart, "The aria2 TLS workflow step is missing.");
        var step = workflow[stepStart..stepEnd];

        Assert.Contains("Invoke-DownKyiTestProject", step, StringComparison.Ordinal);
        Assert.Contains("Assert-DownKyiExpectedTestExecution", step, StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedClassNames DownKyi.Tests.Aria2TlsIntegrationTests",
            step,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SelectSingleNode", step, StringComparison.Ordinal);
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

    private static void AssertWorkflowUsesSharedRunner(
        string relativePath,
        string runnerImport)
    {
        var workflow = Read(relativePath);
        Assert.Contains(runnerImport, workflow, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiTestProject", workflow, StringComparison.Ordinal);
    }

    private static bool ContainsDirectTestEntrypoint(string runScript)
    {
        if (Regex.IsMatch(
                runScript,
                @"(?im)\b(?:vstest\.console|xunit\.console)(?:\.exe)?\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        foreach (var assemblyName in RepositoryTestAssemblyNames)
        {
            if (Regex.IsMatch(
                    runScript,
                    $@"(?im)(?:^|[\s'\""=])(?:[^\r\n\s'\"";|]*[\\/]bin[\\/][^\r\n\s'\"";|]*[\\/]{Regex.Escape(assemblyName)}(?:\.dll|\.exe)?|[^\r\n\s'\"";|]*{Regex.Escape(assemblyName)}\.(?:dll|exe))(?=$|[\s'\"";|])",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        foreach (Match invocation in Regex.Matches(
                     runScript,
                     @"(?im)\bdotnet(?:\.exe)?\s+(?<command>[^\s\)]+)",
                     RegexOptions.CultureInvariant))
        {
            var command = invocation.Groups["command"].Value.Trim('\'', '\"');
            if (command is "restore" or "build" or "publish" or "format" or "tool" or
                "package" or "pack" or "nuget" or "msbuild" or "sln" or "workload" or
                "--info" or "--version")
            {
                continue;
            }

            if (command == "run" && IsStaticallyProvenNonTestRun(runScript, invocation.Index))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsStaticallyProvenNonTestRun(string runScript, int invocationIndex)
    {
        var lineEnd = runScript.IndexOfAny(['\r', '\n'], invocationIndex);
        var invocationLine = lineEnd < 0
            ? runScript[invocationIndex..]
            : runScript[invocationIndex..lineEnd];
        var projectMatch = Regex.Match(
            invocationLine,
            @"--project\s+(?<project>[^\s]+\.csproj)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!projectMatch.Success)
        {
            return false;
        }

        var relativePath = projectMatch.Groups["project"].Value
            .Trim('\'', '\"')
            .Replace('\\', '/')
            .TrimStart('.', '/');
        var fullPath = Path.GetFullPath(relativePath, RepositoryRoot);
        return fullPath.StartsWith(
                   RepositoryRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) &&
               File.Exists(fullPath) &&
               !RepositoryTestProjectPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ExtractWorkflowRunScripts(string workflow)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var scripts = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(
                lines[index],
                @"^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var indentation = match.Groups["indent"].Value.Length;
            var value = match.Groups["value"].Value.Trim();
            if (!value.StartsWith('|') && !value.StartsWith('>'))
            {
                scripts.Add(value);
                continue;
            }

            var blockLines = new List<string>();
            while (++index < lines.Length)
            {
                var line = lines[index];
                if (line.Length == 0)
                {
                    blockLines.Add(string.Empty);
                    continue;
                }

                var contentIndentation = line.Length - line.TrimStart().Length;
                if (contentIndentation <= indentation)
                {
                    index--;
                    break;
                }

                blockLines.Add(line.TrimStart());
            }

            scripts.Add(value.StartsWith('>')
                ? string.Join(' ', blockLines)
                : string.Join('\n', blockLines));
        }

        return scripts;
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
