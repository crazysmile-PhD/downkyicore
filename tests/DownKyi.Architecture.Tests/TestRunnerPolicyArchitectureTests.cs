using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class TestRunnerPolicyArchitectureTests
{
    private static readonly string[] SupportedPlatforms =
        ["Windows", "Linux", "macOS"];

    [Fact]
    public void PolicyIsAStructuredExactAllowlistOfRepositoryTestProjects()
    {
        using var document = JsonDocument.Parse(
            Read("docs/testing/test-runner-policy.json"));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var policyProjects = root.GetProperty("projects")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("project").GetString())
            .Where(path => path != null)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualProjects = Directory
            .EnumerateFiles(
                Path.Combine(GetRepositoryRoot(), "tests"),
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(GetRepositoryRoot(), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actualProjects, policyProjects);
        Assert.Equal(policyProjects.Length, policyProjects.Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in root.GetProperty("projects").EnumerateArray())
        {
            Assert.Equal("xunit-in-process", entry.GetProperty("runner").GetString());
            Assert.Equal("net10.0", entry.GetProperty("targetFramework").GetString());
            Assert.Equal("none", entry.GetProperty("parallel").GetString());
        }
    }

    [Fact]
    public void EveryTestProjectDeclaresOneSupportedPlatformSet()
    {
        foreach (var project in Directory.EnumerateFiles(
                     Path.Combine(GetRepositoryRoot(), "tests"),
                     "*.Tests.csproj",
                     SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(project);
            var platforms = xml.Descendants("DownKyiTestPlatforms")
                .Select(element => element.Value)
                .Single();
            var values = platforms.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.NotEmpty(values);
            Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
            Assert.All(
                values,
                value => Assert.Contains(value, SupportedPlatforms));
        }
    }

    [Fact]
    public void RepositoryEntrypointsDelegateToTheCompiledCentralRunner()
    {
        var project = Read("script/test-project.ps1");
        var solution = Read("script/test-solution.ps1");
        var runner = Read("script/test-project-runner.ps1");
        var targets = Read("Directory.Build.targets");

        Assert.Contains("Invoke-DownKyiTestProject", project, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiTestSolution", solution, StringComparison.Ordinal);
        Assert.Contains(
            "[DownKyi.CentralTestRunner.CentralTestOrchestrator]::RunProjectAsync",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[DownKyi.CentralTestRunner.CentralTestOrchestrator]::RunSolutionAsync",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("EnforceDownKyiCentralTestRunner", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet test", solution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryTestWorkflowsUseOnlyTheRepositoryTestActions()
    {
        foreach (var relativePath in new[]
                 {
                     ".github/workflows/quality.yml",
                     ".github/workflows/build.yml"
                 })
        {
            var source = Read(relativePath);
            Assert.DoesNotContain("dotnet test", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vstest", source, StringComparison.OrdinalIgnoreCase);
        }

        var quality = Read(".github/workflows/quality.yml");
        Assert.Contains("uses: ./.github/actions/test-project", quality, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/actions/test-solution", quality, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformWrappersEstablishLinuxDelegationBeforeLoadingTheRunner()
    {
        foreach (var relativePath in new[]
                 {
                     "script/test-project.ps1",
                     "script/test-solution.ps1",
                     "script/test-assembly-lifecycle.ps1"
                 })
        {
            var source = Read(relativePath);
            var delegation = source.IndexOf(
                "Test-DownKyiDelegatedCgroupScopeRequired",
                StringComparison.Ordinal);
            var runner = source.IndexOf(
                "test-project-runner.ps1",
                StringComparison.Ordinal);

            Assert.True(delegation >= 0, $"Delegation check missing from {relativePath}.");
            Assert.True(runner < 0 || delegation < runner);
        }
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(GetRepositoryRoot(), relativePath));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
