using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class TestRunnerPolicyArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
        var testProjects = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "tests"),
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(testProjects, policyProjects.Keys.Order(StringComparer.Ordinal));
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

        foreach (var workflowPath in workflowPaths)
        {
            var workflow = File.ReadAllText(workflowPath);
            Assert.DoesNotContain(
                ExtractWorkflowRunScripts(workflow),
                ContainsDirectTestEntrypoint);
        }

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
    [InlineData("dotnet vstest $unknownAssembly")]
    [InlineData("dotnet ./tools/xunit.v3.runner.console.dll $unknownAssembly")]
    [InlineData("vstest.console.exe $unknownAssembly")]
    [InlineData("exec dotnet test $unknownTarget -p:DownKyiCentralTestRunner=true")]
    [InlineData("sudo dotnet vstest $unknownAssembly")]
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
        return Regex.IsMatch(
            runScript,
            @"(?im)\bdotnet\s+(?:test|vstest)\b|\bdotnet\s+[^\r\n]*xunit[^\r\n]*\.dll\b|\b(?:vstest\.console|xunit\.console)(?:\.exe)?\b",
            RegexOptions.CultureInvariant);
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

    private static ProcessResult RunDotnet(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
