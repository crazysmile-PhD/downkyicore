using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class ReviewInvariantCorpusTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string CorpusPath = Path.Combine(
        RepositoryRoot,
        "docs",
        "testing",
        "review-invariant-corpus.json");
    private static readonly string[] RequiredPrInvariantIds =
    [
        "cancellation-semantics",
        "runtime-ownership",
        "file-and-output-ownership",
        "failure-classification",
        "ffmpeg-concurrency-budget",
        "json-contract-presence",
        "request-origin-and-credential-contracts",
        "architecture-rule-self-defense",
        "cross-platform-backend-contract",
        "ui-async-state-and-dialogs",
        "host-process-and-log-lifecycle",
        "semantic-version-contract"
    ];
    private static readonly string[] RequiredMainRehearsalEvidenceIds =
    [
        "assembly-lifecycle-stress",
        "real-binary-transfer-security"
    ];

    [Fact]
    public void CorpusReferencesExistingDeterministicTestClasses()
    {
        var corpus = LoadCorpus(CorpusPath);
        var knownClasses = ReadKnownTestClasses();

        var errors = ValidateCorpus(corpus.PrInvariants, knownClasses);

        Assert.Equal(1, corpus.SchemaVersion);
        Assert.Empty(errors);
        Assert.Empty(ValidateEvidence(corpus.MainRehearsalEvidence));
        Assert.Empty(RequiredPrInvariantIds.Except(
            corpus.PrInvariants.Select(invariant => invariant.Id),
            StringComparer.Ordinal));
        Assert.Empty(RequiredMainRehearsalEvidenceIds.Except(
            corpus.MainRehearsalEvidence.Select(evidence => evidence.Id),
            StringComparer.Ordinal));
        Assert.Equal(
            [
                "tests/DownKyi.Application.Tests/DownKyi.Application.Tests.csproj",
                "tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj",
                "tests/DownKyi.Core.Tests/DownKyi.Core.Tests.csproj",
                "tests/DownKyi.Desktop.Tests/DownKyi.Desktop.Tests.csproj",
                "tests/DownKyi.Domain.Tests/DownKyi.Domain.Tests.csproj",
                "tests/DownKyi.Infrastructure.Tests/DownKyi.Infrastructure.Tests.csproj",
                "tests/DownKyi.Tests/DownKyi.Tests.csproj"
            ],
            corpus.PrInvariants
                .SelectMany(invariant => invariant.TestClasses)
                .Select(test => test.Project)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CorpusValidatorFailsClosedForMissingDuplicateOrUnknownCoverage()
    {
        var knownClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Example.Tests.ValidInvariantTests"
        };
        var valid = new ReviewInvariantDefinition(
            "valid",
            ["PR #1"],
            "Guards one root cause.",
            [new ReviewInvariantTestClass("tests/Example.Tests/Example.Tests.csproj", "Example.Tests.ValidInvariantTests")]);
        Assert.Empty(ValidateCorpus([valid], knownClasses, _ => true));

        var errors = ValidateCorpus(
            [
                valid,
                valid,
                new ReviewInvariantDefinition(
                    "empty",
                    [],
                    string.Empty,
                    []),
                new ReviewInvariantDefinition(
                    "unknown",
                    ["PR #2"],
                    "Unknown test must fail.",
                    [new ReviewInvariantTestClass("missing.csproj", "Example.Tests.MissingTests")])
            ],
            knownClasses,
            _ => false);

        Assert.Contains(errors, error => error.Contains("duplicate invariant id", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("historical root", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("guard text", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("test class", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("test project", StringComparison.Ordinal));
    }

    [Fact]
    public void HeavyEvidenceValidatorFailsClosedForMissingProfilesOrWorkflow()
    {
        var valid = new ReviewInvariantEvidenceDefinition(
            "lifecycle",
            ["PR #1"],
            ".github/workflows/quality.yml",
            ["Main", "Rehearsal"],
            "Runs repeated lifecycle checks.");
        Assert.Empty(ValidateEvidence([valid], _ => true));

        var errors = ValidateEvidence(
            [
                valid,
                valid,
                new ReviewInvariantEvidenceDefinition(
                    "missing",
                    [],
                    "missing.yml",
                    ["PR"],
                    string.Empty)
            ],
            _ => false);

        Assert.Contains(errors, error => error.Contains("duplicate evidence id", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("historical root", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("guard text", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("workflow", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Main profile", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Rehearsal profile", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictCiRunsCorpusAndRetainsHeavyMainRehearsalProfiles()
    {
        var quality = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "quality.yml"));
        var release = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));

        Assert.Contains("test-review-invariants.ps1", quality, StringComparison.Ordinal);
        Assert.Contains("windows-latest", quality, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", quality, StringComparison.Ordinal);
        Assert.Contains("macos-latest", quality, StringComparison.Ordinal);
        Assert.Contains("\"Main\"", quality, StringComparison.Ordinal);
        Assert.Contains("-Profile Rehearsal", release, StringComparison.Ordinal);
    }

    private static ReviewInvariantCorpus LoadCorpus(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var invariants = root
            .GetProperty("prInvariants")
            .EnumerateArray()
            .Select(element => new ReviewInvariantDefinition(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("historicalRoots")
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray(),
                element.GetProperty("guards").GetString() ?? string.Empty,
                element.GetProperty("testClasses")
                    .EnumerateArray()
                    .Select(item => new ReviewInvariantTestClass(
                        item.GetProperty("project").GetString() ?? string.Empty,
                        item.GetProperty("class").GetString() ?? string.Empty))
                    .ToArray()))
            .ToArray();
        var evidence = root
            .GetProperty("mainRehearsalEvidence")
            .EnumerateArray()
            .Select(element => new ReviewInvariantEvidenceDefinition(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("historicalRoots")
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray(),
                element.GetProperty("workflow").GetString() ?? string.Empty,
                element.GetProperty("profiles")
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray(),
                element.GetProperty("guards").GetString() ?? string.Empty))
            .ToArray();
        return new ReviewInvariantCorpus(
            root.GetProperty("schemaVersion").GetInt32(),
            invariants,
            evidence);
    }

    private static string[] ValidateCorpus(
        IReadOnlyList<ReviewInvariantDefinition> invariants,
        HashSet<string> knownClasses,
        Func<string, bool>? projectExists = null)
    {
        projectExists ??= project => File.Exists(Path.Combine(RepositoryRoot, project));
        var errors = new List<string>();
        errors.AddRange(invariants
            .GroupBy(invariant => invariant.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate invariant id: {group.Key}"));

        foreach (var invariant in invariants)
        {
            if (invariant.HistoricalRoots.Count == 0)
            {
                errors.Add($"{invariant.Id}: missing historical root");
            }
            if (string.IsNullOrWhiteSpace(invariant.Guards))
            {
                errors.Add($"{invariant.Id}: missing guard text");
            }
            if (invariant.TestClasses.Count == 0)
            {
                errors.Add($"{invariant.Id}: missing test class");
            }

            foreach (var test in invariant.TestClasses)
            {
                if (!projectExists(test.Project))
                {
                    errors.Add($"{invariant.Id}: missing test project {test.Project}");
                }
                if (!knownClasses.Contains(test.Class))
                {
                    errors.Add($"{invariant.Id}: unknown test class {test.Class}");
                }
            }
        }

        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] ValidateEvidence(
        IReadOnlyList<ReviewInvariantEvidenceDefinition> evidence,
        Func<string, bool>? workflowExists = null)
    {
        workflowExists ??= workflow => File.Exists(Path.Combine(RepositoryRoot, workflow));
        var errors = evidence
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate evidence id: {group.Key}")
            .ToList();

        foreach (var item in evidence)
        {
            if (item.HistoricalRoots.Count == 0)
            {
                errors.Add($"{item.Id}: missing historical root");
            }
            if (string.IsNullOrWhiteSpace(item.Guards))
            {
                errors.Add($"{item.Id}: missing guard text");
            }
            if (!workflowExists(item.Workflow))
            {
                errors.Add($"{item.Id}: missing workflow {item.Workflow}");
            }
            if (!item.Profiles.Contains("Main", StringComparer.Ordinal))
            {
                errors.Add($"{item.Id}: missing Main profile");
            }
            if (!item.Profiles.Contains("Rehearsal", StringComparer.Ordinal))
            {
                errors.Add($"{item.Id}: missing Rehearsal profile");
            }
        }

        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    private static HashSet<string> ReadKnownTestClasses()
    {
        return Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => CSharpSourceInspector.ReadTypeDeclarations(File.ReadAllText(path)))
            .Select(declaration => declaration.FullName)
            .ToHashSet(StringComparer.Ordinal);
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

    private sealed record ReviewInvariantCorpus(
        int SchemaVersion,
        IReadOnlyList<ReviewInvariantDefinition> PrInvariants,
        IReadOnlyList<ReviewInvariantEvidenceDefinition> MainRehearsalEvidence);

    private sealed record ReviewInvariantDefinition(
        string Id,
        IReadOnlyList<string> HistoricalRoots,
        string Guards,
        IReadOnlyList<ReviewInvariantTestClass> TestClasses);

    private sealed record ReviewInvariantTestClass(string Project, string Class);

    private sealed record ReviewInvariantEvidenceDefinition(
        string Id,
        IReadOnlyList<string> HistoricalRoots,
        string Workflow,
        IReadOnlyList<string> Profiles,
        string Guards);
}
