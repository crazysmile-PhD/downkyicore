using System.Text.Json;
using DownKyi.CodeMetricsAudit;

namespace DownKyi.Architecture.Tests;

public sealed class CodeMetricsAuditTests
{
    [Fact]
    public void GeneratorDeduplicatesAndSeparatesProductionFromTests()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        var sarifDirectory = directory.CreateDirectory("sarif");
        var productionFile = TemporaryDirectory.CreateFile(repositoryRoot, "src/Product.cs", "internal sealed class Product;");
        var testFile = TemporaryDirectory.CreateFile(repositoryRoot, "tests/ProductTests.cs", "internal sealed class ProductTests;");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            """
            {
              "schemaVersion": 1,
              "production": [
                {
                  "file": "src/Product.cs",
                  "classification": "architecture hotspot",
                  "rationale": "Behavior-led review required."
                }
              ]
            }
            """);
        var productionResult = CreateResult(productionFile, 10, 4, "Production coupling");
        var testResult = CreateResult(testFile, 20, 8, "Test coupling");
        TemporaryDirectory.CreateFile(
            sarifDirectory,
            "Product.sarif",
            CreateSarif(productionResult, productionResult, testResult));

        var report = Ca1506ReportGenerator.Generate(
            repositoryRoot,
            sarifDirectory,
            classifications,
            new GitState("0123456789abcdef", true));

        Assert.Equal(2, report.Summary.Total);
        Assert.Equal(1, report.Summary.Production);
        Assert.Equal(1, report.Summary.Test);
        Assert.Equal(1, report.Summary.Classifications["architecture hotspot"]);
        Assert.Equal(1, report.Summary.Classifications["test integration"]);
        Assert.Contains(report.Findings, finding =>
            finding.File == "src/Product.cs" && finding.Classification == "architecture hotspot");
        Assert.Contains(report.Findings, finding =>
            finding.File == "tests/ProductTests.cs" && finding.Classification == "test integration");
        Assert.All(report.Findings, finding => Assert.False(Path.IsPathRooted(finding.File)));
    }

    [Fact]
    public void WriterProducesHumanAndMachineReadableReports()
    {
        using var directory = new TemporaryDirectory();
        var output = directory.CreateDirectory("output");
        var findings = new[]
        {
            new Ca1506Finding(
                "CA1506",
                "production",
                "needs manual review",
                "Product",
                "src/Product.cs",
                5,
                7,
                "Diagnostic | text",
                "Review rationale")
        };
        var counts = Ca1506ReportGenerator.ClassificationOrder.ToDictionary(
            classification => classification,
            classification => classification == "needs manual review" ? 1 : 0,
            StringComparer.Ordinal);
        var report = new Ca1506Report(
            1,
            "CA1506",
            "0123456789abcdef",
            false,
            new Ca1506Summary(1, 1, 0, counts),
            findings);

        Ca1506ReportWriter.Write(output, report);

        var jsonPath = Path.Combine(output, "ca1506-report.json");
        var markdownPath = Path.Combine(output, "ca1506-report.md");
        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        var markdown = File.ReadAllText(markdownPath);
        Assert.Contains("Production findings: **1**", markdown, StringComparison.Ordinal);
        Assert.Contains("Diagnostic \\| text", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedSarifFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        var sarifDirectory = directory.CreateDirectory("sarif");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            """{"schemaVersion":1,"production":[]}""");
        TemporaryDirectory.CreateFile(sarifDirectory, "Broken.sarif", """{"notRuns":[]}""");

        Assert.Throws<InvalidDataException>(() => Ca1506ReportGenerator.Generate(
            repositoryRoot,
            sarifDirectory,
            classifications,
            new GitState("0123456789abcdef", false)));
    }

    private static object CreateResult(string file, int line, int column, string message)
    {
        return new
        {
            ruleId = "CA1506",
            level = "warning",
            message,
            locations = new[]
            {
                new
                {
                    resultFile = new
                    {
                        uri = new Uri(file).AbsoluteUri,
                        region = new
                        {
                            startLine = line,
                            startColumn = column
                        }
                    }
                }
            }
        };
    }

    private static string CreateSarif(params object[] results)
    {
        return JsonSerializer.Serialize(new
        {
            runs = new[]
            {
                new { results }
            }
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-code-metrics-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_root);
        }

        public string CreateDirectory(string relativePath)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public static string CreateFile(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Temporary test file has no parent directory.");
            Directory.CreateDirectory(parent);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
