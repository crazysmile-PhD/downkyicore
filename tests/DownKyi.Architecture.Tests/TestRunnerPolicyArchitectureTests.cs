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
            var directInvocation = new Regex(
                $"dotnet[ \\t]+test(?:[ \\t]+|\\r?\\n[ \\t]+)[\"']?(?:\\./)?{Regex.Escape(projectPath)}[\"']?",
                RegexOptions.CultureInvariant);

            foreach (var workflowPath in workflowPaths)
            {
                var workflow = File.ReadAllText(workflowPath).Replace('\\', '/');
                Assert.DoesNotMatch(directInvocation, workflow);
            }
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
