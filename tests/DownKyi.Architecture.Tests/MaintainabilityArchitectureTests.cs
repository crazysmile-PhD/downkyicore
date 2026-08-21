namespace DownKyi.Architecture.Tests;

public sealed class MaintainabilityArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void MaintainabilityMetricRulesRemainExplicitlyEnabled()
    {
        var editorConfig = File.ReadAllText(Path.Combine(RepositoryRoot, ".editorconfig"));

        foreach (var rule in new[] { "CA1501", "CA1502", "CA1505", "CA1506", "CA1509" })
        {
            Assert.Contains(
                $"dotnet_diagnostic.{rule}.severity = error",
                editorConfig,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CodeMetricsConfigurationRemainsRegisteredAndFailClosed()
    {
        var buildProps = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var metricsPath = Path.Combine(RepositoryRoot, "CodeMetricsConfig.txt");

        Assert.True(File.Exists(metricsPath), "CodeMetricsConfig.txt must exist.");
        Assert.Contains(
            "<AdditionalFiles Include=\"$(MSBuildThisFileDirectory)CodeMetricsConfig.txt\" />",
            buildProps,
            StringComparison.Ordinal);
        Assert.Contains("<EnableNETAnalyzers>true</EnableNETAnalyzers>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<AnalysisMode>All</AnalysisMode>", buildProps, StringComparison.Ordinal);
        Assert.Contains(
            "<CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>",
            buildProps,
            StringComparison.Ordinal);

        var config = File.ReadAllText(metricsPath);
        Assert.Contains("CA1501: 5", config, StringComparison.Ordinal);
        Assert.Contains("CA1502: 25", config, StringComparison.Ordinal);
        Assert.Contains("CA1505: 10", config, StringComparison.Ordinal);
        Assert.Contains("CA1506(Type): 95", config, StringComparison.Ordinal);
        Assert.Contains("CA1506(Method): 40", config, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
