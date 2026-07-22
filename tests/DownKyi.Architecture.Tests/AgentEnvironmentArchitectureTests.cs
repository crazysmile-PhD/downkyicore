namespace DownKyi.Architecture.Tests;

public sealed class AgentEnvironmentArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void RepositoryStructureSeparatesSourceTestsDocumentationConfigurationAndScripts()
    {
        AssertPathsExist(
            "DownKyi",
            "DownKyi.Core",
            "src",
            "tests",
            "docs",
            "script",
            "Directory.Build.props",
            "Directory.Packages.props");
    }

    [Fact]
    public void RealSystemStateHasObservableEntrypoints()
    {
        AssertPathsExist(
            "script/audit-module-boundaries.ps1",
            "DownKyi.Core/Logging/ApplicationLogProvider.cs",
            "tests/DownKyi.Desktop.Tests/UiSmokeTests.cs",
            "benchmarks/DownKyi.SystemBenchmarks",
            "docs/operations/verification-and-rollback.md",
            "docs/performance-baseline.md");
    }

    [Fact]
    public void BuildRunAndTestInputsAreReproducible()
    {
        AssertPathsExist(
            "global.json",
            "DownKyi.sln",
            "version.txt",
            "Directory.Packages.props",
            "docs/maintenance.md",
            "docs/operations/verification-and-rollback.md");

        var operations = Read("docs/operations/verification-and-rollback.md");
        Assert.Contains("dotnet restore ./DownKyi.sln", operations, StringComparison.Ordinal);
        Assert.Contains("dotnet build ./DownKyi.sln", operations, StringComparison.Ordinal);
        Assert.Contains("dotnet test ./DownKyi.sln", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationAutomaticallyValidatesChanges()
    {
        var qualityWorkflow = Read(".github/workflows/quality.yml");

        Assert.Contains("pull_request:", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("macos-latest", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("--no-incremental", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("AnalysisMode=All", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("Enforce architecture policy", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("--vulnerable", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("--deprecated", qualityWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeStructureHasNavigableEntryPoints()
    {
        AssertPathsExist(
            "README.md",
            "AGENTS.md",
            "ARCHITECTURE.md",
            "docs/ai-knowledge-graph.md",
            "docs/design-docs",
            "docs/exec-plans",
            "docs/product-specs",
            "docs/testing",
            "docs/operations");

        var agentGuide = Read("AGENTS.md");
        Assert.Contains("docs/ai-knowledge-graph.md", agentGuide, StringComparison.Ordinal);
        Assert.Contains("ARCHITECTURE.md", agentGuide, StringComparison.Ordinal);
        Assert.Contains("docs/refactoring-live-plan.md", agentGuide, StringComparison.Ordinal);
        Assert.Contains("docs/operations/verification-and-rollback.md", agentGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeGraphLiteralPathReferencesResolve()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot, "docs", "ai-knowledge-graph.md"));
        var inspectGraph = false;
        var inPaths = false;
        var pathsIndent = 0;
        var missing = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## System Graph", StringComparison.Ordinal))
            {
                inspectGraph = true;
            }

            if (!inspectGraph)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            if (string.Equals(trimmed, "paths:", StringComparison.Ordinal))
            {
                inPaths = true;
                pathsIndent = indent;
                continue;
            }

            if (!inPaths || string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (indent <= pathsIndent)
            {
                inPaths = false;
                continue;
            }

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var value = trimmed[2..].Trim().Trim('`');
            if (!IsLiteralRepositoryPath(value))
            {
                continue;
            }

            if (!Path.Exists(Path.Combine(RepositoryRoot, PathFromRepository(value))))
            {
                missing.Add(value);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Knowledge graph contains stale paths: {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}");
    }

    [Fact]
    public void ModificationScopeHasExecutableBoundaryRatchets()
    {
        AssertPathsExist(
            "tests/DownKyi.Architecture.Tests/ProjectDependencyTests.cs",
            "tests/DownKyi.Architecture.Tests/ModuleBoundaryBaselineTests.cs",
            "docs/testing/module-boundary-ratchets.md",
            "docs/design-docs/module-boundary-naming-audit.md");

        var ratchets = Read("tests/DownKyi.Architecture.Tests/ModuleBoundaryBaselineTests.cs");
        Assert.Contains("CoreUiDependenciesCannotGrowBeyondTheKnownBaseline", ratchets, StringComparison.Ordinal);
        Assert.Contains("ServiceContractsCannotAddPresentationDependencies", ratchets, StringComparison.Ordinal);
        Assert.Contains("OversizedProductionFilesCannotGrowBeyondTheKnownBaseline", ratchets, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureBehaviorPolicyRejectsSilentAndBlockingPatterns()
    {
        AssertPathsExist(
            "tests/DownKyi.Architecture.Tests/LegacyPatternArchitectureTests.cs",
            "tests/DownKyi.Core.Tests/WebClientTests.cs",
            "tests/DownKyi.Core.Tests/BiliApiContractSampleTests.cs");

        var policy = Read("tests/DownKyi.Architecture.Tests/LegacyPatternArchitectureTests.cs");
        Assert.Contains("Thread.Sleep", policy, StringComparison.Ordinal);
        Assert.Contains("GetAwaiter.GetResult", policy, StringComparison.Ordinal);
        Assert.Contains("EmptyCatchBlocks", policy, StringComparison.Ordinal);

        var architecture = Read("ARCHITECTURE.md");
        Assert.Contains("typed result", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancellation", architecture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceWorkflowsAndRollbackAreStandardized()
    {
        AssertPathsExist(
            "docs/maintenance.md",
            "docs/operations/verification-and-rollback.md",
            "docs/product-specs/v1.1.0-release-gate.md",
            "script/validate-publish-output.ps1",
            ".github/workflows/build.yml");

        var operations = Read("docs/operations/verification-and-rollback.md");
        Assert.Contains("git revert <commit-sha>", operations, StringComparison.Ordinal);
        Assert.Contains("migration", operations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rollback", operations, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPathsExist(params string[] relativePaths)
    {
        var missing = relativePaths
            .Where(path => !Path.Exists(Path.Combine(RepositoryRoot, PathFromRepository(path))))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing repository entry points: {string.Join(", ", missing)}");
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, PathFromRepository(relativePath)));
    }

    private static string PathFromRepository(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsLiteralRepositoryPath(string value)
    {
        return value.Contains('/', StringComparison.Ordinal) &&
               !value.Contains('*', StringComparison.Ordinal) &&
               !value.Contains(" + ", StringComparison.Ordinal) &&
               !value.Contains(" and ", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("://", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
