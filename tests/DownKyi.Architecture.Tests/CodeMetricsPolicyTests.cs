namespace DownKyi.Architecture.Tests;

public sealed class CodeMetricsPolicyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TierOneLegacyRulesAreBlockingWithoutEnablingCa1501OrCa1506()
    {
        var editorConfig = Read(".editorconfig");
        string[] blockingRules =
        [
            "CA1005",
            "CA1017",
            "CA1021",
            "CA1045",
            "CA1060",
            "CA1502",
            "CA1505",
            "CA1509"
        ];

        foreach (var rule in blockingRules)
        {
            Assert.Contains($"dotnet_diagnostic.{rule}.severity = error", editorConfig, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("dotnet_diagnostic.CA1501.severity", editorConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet_diagnostic.CA1506.severity", editorConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void Ca1506AuditIsIsolatedNonBlockingAndMachineReadable()
    {
        AssertPathsExist(
            "script/audit-code-metrics.ps1",
            "script/code-metrics/ca1506.globalconfig",
            "script/code-metrics/ca1506.targets",
            "script/code-metrics/ca1506-classifications.json",
            "tools/DownKyi.CodeMetricsAudit/DownKyi.CodeMetricsAudit.csproj",
            "tools/DownKyi.CodeMetricsAudit/Ca1506ReportGenerator.cs",
            "tools/DownKyi.CodeMetricsAudit/Ca1506ReportWriter.cs",
            "docs/testing/code-metrics-audit.md");

        var script = Read("script/audit-code-metrics.ps1");
        Assert.Contains("-p:TreatWarningsAsErrors=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:CodeAnalysisTreatWarningsAsErrors=false", script, StringComparison.Ordinal);
        Assert.Contains("tools/DownKyi.CodeMetricsAudit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertTo-Json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ca1506-report.json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ca1506-report.md", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Select-String", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ForEach-Object", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" git ", script, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            script.Split('\n').Length <= 90,
            "The PowerShell audit entry must remain an extremely thin process boundary.");

        var generator = Read("tools/DownKyi.CodeMetricsAudit/Ca1506ReportGenerator.cs");
        Assert.Contains("ruleId", generator, StringComparison.Ordinal);
        Assert.Contains("findingsByKey", generator, StringComparison.Ordinal);
        Assert.Contains("test integration", generator, StringComparison.Ordinal);

        var writer = Read("tools/DownKyi.CodeMetricsAudit/Ca1506ReportWriter.cs");
        Assert.Contains("ca1506-report.json", writer, StringComparison.Ordinal);
        Assert.Contains("ca1506-report.md", writer, StringComparison.Ordinal);

        var workflow = Read(".github/workflows/quality.yml");
        Assert.Contains("./script/audit-code-metrics.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("ca1506-architecture-audit", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", ExtractCodeMetricsJob(workflow), StringComparison.Ordinal);
    }

    private static void AssertPathsExist(params string[] relativePaths)
    {
        var missing = relativePaths
            .Where(path => !Path.Exists(Path.Combine(RepositoryRoot, PathFromRepository(path))))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing CA1506 audit files: {string.Join(", ", missing)}");
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, PathFromRepository(relativePath)));
    }

    private static string ExtractCodeMetricsJob(string workflow)
    {
        const string jobHeader = "  code-metrics-audit:";
        var lines = workflow.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => string.Equals(line, jobHeader, StringComparison.Ordinal));
        Assert.True(start >= 0, "The CA1506 audit job is missing.");
        var end = Array.FindIndex(
            lines,
            start + 1,
            line => line.StartsWith("  ", StringComparison.Ordinal) &&
                !line.StartsWith("    ", StringComparison.Ordinal) &&
                line.EndsWith(':'));
        return string.Join('\n', end < 0 ? lines[start..] : lines[start..end]);
    }

    private static string PathFromRepository(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
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
