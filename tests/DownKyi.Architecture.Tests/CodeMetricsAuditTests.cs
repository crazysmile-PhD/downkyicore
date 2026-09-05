using System.Diagnostics;
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
              "schemaVersion": 2,
              "production": [
                {
                  "file": "src/Product.cs",
                  "identity": "location:10:4",
                  "classification": "architecture hotspot",
                  "rationale": "Behavior-led review required."
                }
              ]
            }
            """);
        var productionResult = CreateResult(productionFile, 10, 4, "'Product' is coupled with production types.");
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

        Assert.True(report.DirtyWorktree);
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
    public void WriterRollsBackBothReportsWhenSecondPublicationFails()
    {
        using var directory = new TemporaryDirectory();
        var output = directory.CreateDirectory("output");
        var jsonPath = TemporaryDirectory.CreateFile(output, "ca1506-report.json", "previous-json");
        var markdownPath = TemporaryDirectory.CreateFile(output, "ca1506-report.md", "previous-markdown");
        var report = CreateReport();

        Assert.Throws<IOException>(() => Ca1506ReportWriter.Write(
            output,
            report,
            (source, destination) =>
            {
                if (destination.EndsWith(".md", StringComparison.Ordinal))
                {
                    throw new IOException("Injected Markdown publication failure.");
                }

                File.Move(source, destination);
            }));

        Assert.Equal("previous-json", File.ReadAllText(jsonPath));
        Assert.Equal("previous-markdown", File.ReadAllText(markdownPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output),
            path => Path.GetFileName(path).StartsWith(".ca1506-report-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyntacticallyMalformedJsonFailsWithBoundedOutput(bool malformedSarif)
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        InitializeGitRepository(repositoryRoot);
        var sarifDirectory = directory.CreateDirectory("sarif");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            malformedSarif ? """{"schemaVersion":2,"production":[]}""" : "{broken");
        TemporaryDirectory.CreateFile(
            sarifDirectory,
            "Broken.sarif",
            malformedSarif ? "{broken" : CreateSarif());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--repository-root", repositoryRoot,
                "--sarif-directory", sarifDirectory,
                "--classification-file", classifications,
                "--output-directory", Path.Combine(repositoryRoot, "reports")
            ],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal("CA1506 audit failed: malformed JSON input.", error.ToString().Trim());
        Assert.DoesNotContain(repositoryRoot, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(JsonException), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticLocationSeparatesSameNamedMembersAndIsOrderIndependent()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        var sarifDirectory = directory.CreateDirectory("sarif");
        var productionFile = TemporaryDirectory.CreateFile(repositoryRoot, "src/Product.cs", "internal sealed class Product;");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            """
            {
              "schemaVersion": 2,
              "production": [
                {
                  "file": "src/Product.cs",
                  "identity": "location:10:4",
                  "classification": "architecture hotspot",
                  "rationale": "Reviewed member rationale."
                }
              ]
            }
            """);
        var first = CreateResult(productionFile, 10, 4, "'Execute' on ProductA is coupled with reviewed types.");
        var second = CreateResult(productionFile, 20, 4, "'Execute' on ProductB is coupled with new types.");
        var overload = CreateResult(productionFile, 30, 4, "'Execute' overload is coupled with new types.");
        var sarifPath = TemporaryDirectory.CreateFile(
            sarifDirectory,
            "Product.sarif",
            CreateSarif(first, second, overload));

        var reportBeforeReorder = Ca1506ReportGenerator.Generate(
            repositoryRoot,
            sarifDirectory,
            classifications,
            new GitState("0123456789abcdef", false));

        File.WriteAllText(sarifPath, CreateSarif(overload, first, second));
        var reportAfterReorder = Ca1506ReportGenerator.Generate(
            repositoryRoot,
            sarifDirectory,
            classifications,
            new GitState("0123456789abcdef", false));

        var reviewed = Assert.Single(reportBeforeReorder.Findings, finding => finding.Line == 10);
        Assert.Equal("architecture hotspot", reviewed.Classification);
        Assert.Equal("Reviewed member rationale.", reviewed.Rationale);
        Assert.All(
            reportBeforeReorder.Findings.Where(finding => finding.Line is 20 or 30),
            finding => Assert.Equal("needs manual review", finding.Classification));
        Assert.Equal(
            reportBeforeReorder.Findings.Select(ToClassificationIdentity),
            reportAfterReorder.Findings.Select(ToClassificationIdentity));
    }

    [Fact]
    public async Task SuccessfulAuditReportsFixedNamesWithoutAbsolutePaths()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        InitializeGitRepository(repositoryRoot);
        var sarifDirectory = directory.CreateDirectory("sarif");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            """{"schemaVersion":2,"production":[]}""");
        TemporaryDirectory.CreateFile(sarifDirectory, "Product.sarif", CreateSarif());
        var outputDirectory = Path.Combine(repositoryRoot, "artifacts", "code-metrics");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--repository-root", repositoryRoot,
                "--sarif-directory", sarifDirectory,
                "--classification-file", classifications,
                "--output-directory", outputDirectory
            ],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.DoesNotContain(repositoryRoot, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ca1506-report.md", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ca1506-report.json", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "ca1506-report.md")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "ca1506-report.json")));
    }

    [Fact]
    public async Task UntrackedCompileInputMarksGitStateDirty()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        InitializeGitRepository(repositoryRoot);

        Assert.False((await GitStateReader.ReadAsync(repositoryRoot)).DirtyWorktree);

        TemporaryDirectory.CreateFile(repositoryRoot, "src/UntrackedCompileInput.cs", "internal sealed class UntrackedCompileInput;");

        Assert.True((await GitStateReader.ReadAsync(repositoryRoot)).DirtyWorktree);
    }

    [Fact]
    public async Task IgnoredCompileInputMarksGitStateDirty()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        InitializeGitRepository(repositoryRoot);
        TemporaryDirectory.CreateFile(repositoryRoot, ".gitignore", "DownKyi.Core/Binary/*\n");
        RunGit(repositoryRoot, "add", ".gitignore");
        RunGit(repositoryRoot, "commit", "--quiet", "-m", "ignore fixture source");

        Assert.False((await GitStateReader.ReadAsync(repositoryRoot)).DirtyWorktree);

        TemporaryDirectory.CreateFile(
            repositoryRoot,
            "DownKyi.Core/Binary/CompileInput.cs",
            "internal sealed class CompileInput;");

        Assert.True((await GitStateReader.ReadAsync(repositoryRoot)).DirtyWorktree);
    }

    [Fact]
    public async Task MissingInputPathFailsWithoutDisclosingAbsolutePath()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        var missingPath = Path.Combine(repositoryRoot, "missing-sarif");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--repository-root", repositoryRoot,
                "--sarif-directory", missingPath,
                "--classification-file", missingPath,
                "--output-directory", Path.Combine(repositoryRoot, "reports")
            ],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal("CA1506 audit failed: invalid arguments or missing input.", error.ToString().Trim());
        Assert.DoesNotContain(repositoryRoot, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            error.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputIoFailureDoesNotDiscloseAbsolutePath()
    {
        using var directory = new TemporaryDirectory();
        var repositoryRoot = directory.CreateDirectory("repository");
        InitializeGitRepository(repositoryRoot);
        var sarifDirectory = directory.CreateDirectory("sarif");
        var classifications = TemporaryDirectory.CreateFile(
            repositoryRoot,
            "classifications.json",
            """{"schemaVersion":2,"production":[]}""");
        TemporaryDirectory.CreateFile(sarifDirectory, "Product.sarif", CreateSarif());
        var outputPath = TemporaryDirectory.CreateFile(repositoryRoot, "blocked-output", "not a directory");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--repository-root", repositoryRoot,
                "--sarif-directory", sarifDirectory,
                "--classification-file", classifications,
                "--output-directory", outputPath
            ],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal("CA1506 audit failed: audit I/O failure.", error.ToString().Trim());
        Assert.DoesNotContain(repositoryRoot, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            error.ToString(),
            StringComparison.OrdinalIgnoreCase);
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
            """{"schemaVersion":2,"production":[]}""");
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

    private static string ToClassificationIdentity(Ca1506Finding finding)
    {
        return $"{finding.File}|{finding.Line}|{finding.Column}|{finding.Classification}|{finding.Rationale}";
    }

    private static Ca1506Report CreateReport()
    {
        var counts = Ca1506ReportGenerator.ClassificationOrder.ToDictionary(
            classification => classification,
            _ => 0,
            StringComparer.Ordinal);
        return new Ca1506Report(
            1,
            "CA1506",
            "0123456789abcdef",
            false,
            new Ca1506Summary(0, 0, 0, counts),
            []);
    }

    private static void InitializeGitRepository(string repositoryRoot)
    {
        RunGit(repositoryRoot, "init", "--quiet");
        RunGit(repositoryRoot, "config", "user.email", "tests@example.invalid");
        RunGit(repositoryRoot, "config", "user.name", "DownKyi Tests");
        TemporaryDirectory.CreateFile(repositoryRoot, "README.md", "fixture");
        RunGit(repositoryRoot, "add", "README.md");
        RunGit(repositoryRoot, "commit", "--quiet", "-m", "fixture");
    }

    private static void RunGit(string repositoryRoot, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
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
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
