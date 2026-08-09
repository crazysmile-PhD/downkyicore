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

    [Fact]
    public void CorpusAndPolicyRemainFailClosed()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CorpusPath));
        var root = document.RootElement;
        var invariants = root.GetProperty("prInvariants").EnumerateArray().ToArray();
        var evidence = root.GetProperty("mainRehearsalEvidence").EnumerateArray().ToArray();

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(invariants);
        Assert.Equal(
            invariants.Length,
            invariants.Select(item => item.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());

        foreach (var invariant in invariants)
        {
            Assert.False(string.IsNullOrWhiteSpace(invariant.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(invariant.GetProperty("guards").GetString()));
            Assert.NotEmpty(invariant.GetProperty("historicalRoots").EnumerateArray());
            var tests = invariant.GetProperty("testClasses").EnumerateArray().ToArray();
            Assert.NotEmpty(tests);
            foreach (var test in tests)
            {
                var project = test.GetProperty("project").GetString();
                Assert.False(string.IsNullOrWhiteSpace(test.GetProperty("class").GetString()));
                Assert.True(File.Exists(Path.Combine(RepositoryRoot, project!)), project);
            }
        }

        Assert.Contains(evidence, item =>
            item.GetProperty("profiles").EnumerateArray().Any(profile =>
                profile.GetString() == "Main") &&
            item.GetProperty("profiles").EnumerateArray().Any(profile =>
                profile.GetString() == "Rehearsal"));

        var policy = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docs",
            "testing",
            "review-invariant-policy.md"));
        Assert.Contains("Identify the violated invariant", policy, StringComparison.Ordinal);
        Assert.Contains("Search sibling paths", policy, StringComparison.Ordinal);
        Assert.Contains("Failure And Transition Matrix", policy, StringComparison.Ordinal);
        Assert.Contains("Repeated-Review Escalation", policy, StringComparison.Ordinal);
        Assert.Contains("Scope Containment", policy, StringComparison.Ordinal);
        Assert.Contains("does not automatically", policy, StringComparison.Ordinal);

        var agents = File.ReadAllText(Path.Combine(RepositoryRoot, "AGENTS.md"));
        Assert.Contains("Review Remediation Gate", agents, StringComparison.Ordinal);
        Assert.Contains("停止 local patch", agents, StringComparison.Ordinal);
        Assert.Contains("不能自動擴大目前 PR 的修改範圍", agents, StringComparison.Ordinal);
        Assert.Contains("backlog 或 separate PR", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictCiRunsCorpusOnEverySupportedOperatingSystem()
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
