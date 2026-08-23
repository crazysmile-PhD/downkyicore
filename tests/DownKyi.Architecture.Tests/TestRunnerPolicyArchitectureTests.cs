using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class TestRunnerPolicyArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DownKyiTestsUsesTheInProcessRunnerForVstestProtocolSafety()
    {
        using var policy = JsonDocument.Parse(Read("docs/testing/test-runner-policy.json"));
        var project = policy.RootElement
            .GetProperty("projects")
            .EnumerateArray()
            .Single(entry => string.Equals(
                entry.GetProperty("project").GetString(),
                "tests/DownKyi.Tests/DownKyi.Tests.csproj",
                StringComparison.Ordinal));

        Assert.Equal("xunit-in-process", project.GetProperty("runner").GetString());
        Assert.Equal("net10.0", project.GetProperty("targetFramework").GetString());
        Assert.Equal("none", project.GetProperty("parallel").GetString());

        var reason = project.GetProperty("reason").GetString();
        Assert.Contains("xunit/xunit#3576", reason, StringComparison.Ordinal);
        Assert.Contains("assembly-info stdout protocol corruption", reason, StringComparison.Ordinal);
        Assert.Contains("lifecycle", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separately verified", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aria2TlsWorkflowUsesTheSharedRunnerAndExactTestClass()
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

        Assert.Contains("shell: pwsh", step, StringComparison.Ordinal);
        Assert.Contains(". ./script/test-project-runner.ps1", step, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiTestProject", step, StringComparison.Ordinal);
        Assert.Contains(
            "-ProjectPath ./tests/DownKyi.Tests/DownKyi.Tests.csproj",
            step,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ClassNames DownKyi.Tests.Aria2TlsIntegrationTests",
            step,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--filter Category=Aria2TlsIntegration", step, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyOwnedProjectsCannotBypassTheSharedRunnerInCiWorkflows()
    {
        using var policy = JsonDocument.Parse(Read("docs/testing/test-runner-policy.json"));
        var workflowPaths = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, ".github", "workflows"),
                "*.y*ml",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var project in policy.RootElement.GetProperty("projects").EnumerateArray())
        {
            var projectPath = project.GetProperty("project").GetString()
                ?? throw new InvalidDataException("Runner policy project path cannot be null.");

            foreach (var workflowPath in workflowPaths)
            {
                var workflow = File.ReadAllText(workflowPath);
                Assert.DoesNotContain(
                    ExtractWorkflowRunScripts(workflow),
                    runScript => ContainsDirectDotnetTestInvocation(runScript, projectPath));
            }
        }
    }

    [Theory]
    [InlineData("steps:\n  - run: dotnet test ./tests/DownKyi.Tests/DownKyi.Tests.csproj --no-build")]
    [InlineData("steps:\n  - run: dotnet test --no-build ./tests/DownKyi.Tests/DownKyi.Tests.csproj")]
    [InlineData("steps:\n  - run: >\n      dotnet test\n      --no-build\n      ./tests/DownKyi.Tests/DownKyi.Tests.csproj")]
    [InlineData("steps:\n  - run: |\n      dotnet test `\n        --no-build `\n        ./tests/DownKyi.Tests/DownKyi.Tests.csproj")]
    [InlineData("steps:\n  - run: |\n      dotnet test \\\n        --no-build \\\n        ./tests/DownKyi.Tests/DownKyi.Tests.csproj")]
    public void DirectTestInvocationDetectorRejectsRepresentativeOptionOrderings(string workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var runScript = Assert.Single(ExtractWorkflowRunScripts(workflow));

        Assert.True(ContainsDirectDotnetTestInvocation(
            runScript,
            "tests/DownKyi.Tests/DownKyi.Tests.csproj"));
    }

    [Fact]
    public void DirectTestInvocationDetectorAllowsTheSharedRunner()
    {
        const string workflow = """
            steps:
              - run: |
                  . ./script/test-project-runner.ps1
                  Invoke-DownKyiTestProject `
                    -ProjectPath ./tests/DownKyi.Tests/DownKyi.Tests.csproj `
                    -ClassNames DownKyi.Tests.Aria2TlsIntegrationTests
            """;
        var runScript = Assert.Single(ExtractWorkflowRunScripts(workflow));

        Assert.False(ContainsDirectDotnetTestInvocation(
            runScript,
            "tests/DownKyi.Tests/DownKyi.Tests.csproj"));
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

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
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

    private static bool ContainsDirectDotnetTestInvocation(string runScript, string projectPath)
    {
        var normalized = runScript
            .Replace("\\\n", " ", StringComparison.Ordinal)
            .Replace("`\n", " ", StringComparison.Ordinal)
            .Replace('\\', '/');
        var commands = Regex.Matches(
            normalized,
            @"(?im)(?:^|[;&|]\s*)dotnet\s+test\b(?<arguments>[^\r\n;&|]*)",
            RegexOptions.CultureInvariant);

        return commands.Any(command => command.Groups["arguments"].Value.Contains(
            projectPath,
            StringComparison.OrdinalIgnoreCase));
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
