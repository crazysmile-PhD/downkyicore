namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleProbeBehaviorTests
{
    private const string MutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_LEASE";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void LifecycleForensicsIsAnObserverRatherThanASecondProcessOwner()
    {
        var source = ReadLifecycleGate();
        var observer = ReadFunction(source, "Invoke-ForensicsObserverCapture");
        string[] forbiddenObserverAuthorities =
        [
            "ownedTreeQuiescent",
            "residualChildren",
            "OwnedProcessLease",
            "CompleteEvidenceHoldAsync",
            "TransitionBudget]::Start",
            ".WaitAsync(",
            ".Kill(",
            ".Terminate("
        ];

        foreach (var authority in forbiddenObserverAuthorities)
        {
            Assert.DoesNotContain(authority, observer, StringComparison.Ordinal);
        }

        Assert.Contains("EvidenceHoldRequest", source, StringComparison.Ordinal);
        Assert.Contains("CompleteEvidenceHoldAsync", source, StringComparison.Ordinal);
        Assert.Contains("EvidenceCaptureCompletion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function New-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function Start-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function Complete-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function Close-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedChildReleaseLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-ResidualProcessTree", source, StringComparison.Ordinal);

        var probe = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.AssemblyLifecycleProbe",
            "Program.cs"));
        Assert.DoesNotContain("DOWNKYI_TRANSIENT_CHILD", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Kill(entireProcessTree", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverTruthMutationCannotSatisfyTheProcessOwnerGate()
    {
        var source = ReadLifecycleGate();
        if (string.Equals(
                Environment.GetEnvironmentVariable(MutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            source = source.Replace(
                "-not $ProcessResult.ownedTreeQuiescent -or",
                "(-not $ProcessResult.ownedTreeQuiescent -and " +
                "-not $ProcessResult.observerTreeQuiescent) -or",
                StringComparison.Ordinal);
        }

        var classifier = ReadFunction(source, "New-ProcessPhaseResult");
        Assert.Contains(
            "-not $ProcessResult.ownedTreeQuiescent -or",
            classifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$ProcessResult.observerTreeQuiescent",
            classifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$ProcessResult.diagnosticTreeQuiescent",
            classifier,
            StringComparison.Ordinal);
    }

    private static string ReadFunction(string source, string functionName)
    {
        var startToken = $"function {functionName} {{";
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Lifecycle function was not found: {functionName}");
        var next = source.IndexOf("\nfunction ", start + startToken.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string ReadLifecycleGate()
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "test-assembly-lifecycle.ps1"));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DownKyi.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
