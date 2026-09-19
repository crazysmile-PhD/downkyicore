using System.Diagnostics;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class LegacyCaReportTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public async Task ReportPreservesRawFindingsWhileClassifyingBaselineNewAndWorsenedMetrics()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"downkyi-legacy-ca-report-{Guid.NewGuid():N}");
        var sarifDirectory = Path.Combine(temporaryRoot, "sarif");
        var outputDirectory = Path.Combine(temporaryRoot, "output");
        var baselinePath = Path.Combine(temporaryRoot, "baseline.json");
        Directory.CreateDirectory(sarifDirectory);

        try
        {
            var inheritanceChain = "UserControl, ContentControl, TemplatedControl, Control, InputElement, Interactive, Layoutable, Visual, StyledElement, Animatable, AvaloniaObject";
            WriteSarif(
                Path.Combine(sarifDirectory, "project-a.sarif"),
                "project-a",
                new SyntheticFinding("CA1501", Path.Combine(temporaryRoot, "src", "ExistingView.cs"), 4, "'ExistingView' has an object hierarchy of '11', which is greater than '6': '" + inheritanceChain + "'."),
                new SyntheticFinding("CA1501", Path.Combine(temporaryRoot, "src", "NewView.cs"), 5, "'NewView' has an object hierarchy of '12', which is greater than '6': '" + inheritanceChain + "'."),
                new SyntheticFinding("CA1506", Path.Combine(temporaryRoot, "tests", "SharedTests.cs"), 6, "'SharedOperation' is coupled with '42' different types from '12' namespaces. Rewrite or refactor the method to decrease its class coupling below '41'."));
            WriteSarif(
                Path.Combine(sarifDirectory, "project-b.sarif"),
                "project-b",
                new SyntheticFinding("CA1506", Path.Combine(temporaryRoot, "tests", "SharedTests.cs"), 6, "'SharedOperation' is coupled with '42' different types from '12' namespaces. Rewrite or refactor the method to decrease its class coupling below '41'."));
            WriteBaseline(baselinePath);

            var result = await RunReporterAsync(sarifDirectory, outputDirectory, baselinePath, temporaryRoot).ConfigureAwait(true);
            Assert.True(result.ExitCode == 0, $"Reporter failed. stdout: {result.StandardOutput}\nstderr: {result.StandardError}");

            var reportPath = Path.Combine(outputDirectory, "legacy-ca-report.json");
            var reportJson = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var report = JsonDocument.Parse(reportJson);
            var root = report.RootElement;
            var rawFindings = root.GetProperty("rawFindings").EnumerateArray().ToArray();
            var findings = root.GetProperty("findings").EnumerateArray().ToArray();
            var groups = root.GetProperty("groups").EnumerateArray().ToArray();

            Assert.Equal(4, rawFindings.Length);
            Assert.Equal(3, findings.Length);
            Assert.All(rawFindings, finding =>
            {
                Assert.True(finding.TryGetProperty("sourceResult", out var sourceResult));
                Assert.True(sourceResult.TryGetProperty("properties", out var properties));
                Assert.False(string.IsNullOrWhiteSpace(properties.GetProperty("fixtureMarker").GetString()));
            });

            Assert.Equal("baseline", FindBySymbol(findings, "ExistingView").GetProperty("status").GetString());
            Assert.Equal("new", FindBySymbol(findings, "NewView").GetProperty("status").GetString());
            var sharedFinding = FindBySymbol(findings, "SharedOperation");
            Assert.Equal("metric-worsened", sharedFinding.GetProperty("status").GetString());
            Assert.Equal(1, sharedFinding.GetProperty("metricDelta").GetInt32());
            Assert.Equal(2, sharedFinding.GetProperty("rawFindingIds").GetArrayLength());

            var rawIds = IdSet(rawFindings.Select(finding => finding.GetProperty("id")));
            var findingRawIds = IdSet(findings.SelectMany(finding => finding.GetProperty("rawFindingIds").EnumerateArray()));
            var groupRawIds = IdSet(groups.SelectMany(group => group.GetProperty("rawFindingIds").EnumerateArray()));
            Assert.True(rawIds.SetEquals(findingRawIds));
            Assert.True(rawIds.SetEquals(groupRawIds));

            Assert.All(groups.SelectMany(group => group.GetProperty("members").EnumerateArray()), member =>
            {
                Assert.False(string.IsNullOrWhiteSpace(member.GetProperty("findingId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(member.GetProperty("file").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(member.GetProperty("symbol").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(member.GetProperty("rule").GetString()));
                Assert.True(member.GetProperty("rawFindingIds").GetArrayLength() > 0);
            });
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static HashSet<string> IdSet(IEnumerable<JsonElement> ids) =>
        ids.Select(id => id.GetString() ?? throw new InvalidDataException("Finding ID must not be null."))
            .ToHashSet(StringComparer.Ordinal);

    private static JsonElement FindBySymbol(IEnumerable<JsonElement> findings, string symbol) =>
        findings.Single(finding => string.Equals(finding.GetProperty("symbol").GetString(), symbol, StringComparison.Ordinal));

    private static void WriteSarif(string path, string fixtureMarker, params SyntheticFinding[] findings)
    {
        var results = findings.Select(finding => new
        {
            ruleId = finding.Rule,
            level = "warning",
            message = finding.Message,
            locations = new[] { new { resultFile = new { uri = new Uri(finding.Path).AbsoluteUri, region = new { startLine = finding.Line, startColumn = 1 } } } },
            properties = new { fixtureMarker }
        });
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = "1.0.0", runs = new[] { new { results } } }));
    }

    private static void WriteBaseline(string path)
    {
        var findings = new object[]
        {
            new
            {
                identity = "CA1501|src/ExistingView.cs|ExistingView", rule = "CA1501", path = "src/ExistingView.cs", symbol = "ExistingView", observedMetric = 11,
                groupId = "baseline-existing-view", groupTitle = "Existing framework inheritance", classification = "framework-inheritance", reviewStatus = "reviewed-reasonable", rationale = "Fixture baseline."
            },
            new
            {
                identity = "CA1506|tests/SharedTests.cs|SharedOperation", rule = "CA1506", path = "tests/SharedTests.cs", symbol = "SharedOperation", observedMetric = 41,
                groupId = "baseline-shared-operation", groupTitle = "Existing integration test", classification = "integration-test", reviewStatus = "reviewed-reasonable", rationale = "Fixture baseline."
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, findings }));
    }

    private static async Task<ProcessResult> RunReporterAsync(string sarifDirectory, string outputDirectory, string baselinePath, string repositoryRoot)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-File", Path.Combine(RepositoryRoot, "script", "code-metrics", "report-legacy-ca.ps1"),
            "-SarifDirectory", sarifDirectory, "-OutputDirectory", outputDirectory,
            "-BaselinePath", baselinePath, "-RepositoryRoot", repositoryRoot
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(true);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(true),
            await standardError.ConfigureAwait(true));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record SyntheticFinding(string Rule, string Path, int Line, string Message);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
