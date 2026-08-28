using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleProbeBehaviorTests
{
    private const string MutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_LEASE";
    private const string HelperAuthorityMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_HELPER_AUTHORITY";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void LifecycleForensicsIsAnObserverRatherThanASecondProcessOwner()
    {
        var source = ReadLifecycleGate();
        if (string.Equals(
                Environment.GetEnvironmentVariable(HelperAuthorityMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            source = source.Replace(
                "$safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'",
                "$Process.Kill($true)\n    " +
                "$safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'",
                StringComparison.Ordinal);
        }

        var observerClosure = ReadFunctionClosure(source, "Invoke-ForensicsObserverCapture");
        string[] forbiddenObserverAuthorities =
        [
            "ownedTreeQuiescent",
            "residualChildren",
            "OwnedProcessLease",
            "CompleteEvidenceHoldAsync",
            "TransitionBudget]::Start",
            ".Terminate("
        ];

        foreach (var function in observerClosure)
        {
            foreach (var authority in forbiddenObserverAuthorities)
            {
                Assert.DoesNotContain(authority, function.Value, StringComparison.Ordinal);
            }

            if (!string.Equals(
                    function.Key,
                    "Stop-BoundedForensicsCollector",
                    StringComparison.Ordinal))
            {
                Assert.DoesNotContain(".Kill(", function.Value, StringComparison.Ordinal);
            }
        }

        Assert.Contains("Save-ProcessEvidence", observerClosure.Keys);
        Assert.Contains("Get-DiagnosticProcessTreeSnapshot", observerClosure.Keys);
        Assert.Contains("Invoke-BoundedForensicsCollector", observerClosure.Keys);
        var collectorCleanup = observerClosure["Stop-BoundedForensicsCollector"];
        Assert.Contains("$Collector.Kill($true)", collectorCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetProcessId", collectorCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("$Process.", collectorCleanup, StringComparison.Ordinal);

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
    public void ForensicsCollectorWaitsAreCancellationAwareAndCleanupAlwaysAttemptsReap()
    {
        var source = ReadLifecycleGate();
        var collector = ReadFunction(source, "Invoke-BoundedForensicsCollector");
        var cleanup = ReadFunction(source, "Stop-BoundedForensicsCollector");

        Assert.DoesNotContain(".WaitForExit(", collector, StringComparison.Ordinal);
        Assert.DoesNotContain(".WaitForExit(", cleanup, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync($CancellationToken)", collector, StringComparison.Ordinal);
        Assert.Contains("$outputTasks.WaitAsync(", collector, StringComparison.Ordinal);
        Assert.Contains("$CancellationToken", collector, StringComparison.Ordinal);
        Assert.Contains("$Collector.Kill($true)", cleanup, StringComparison.Ordinal);
        Assert.Contains(
            "$Collector.WaitForExitAsync([Threading.CancellationToken]::None)",
            cleanup,
            StringComparison.Ordinal);
        Assert.True(
            cleanup.IndexOf("$Collector.Kill($true)", StringComparison.Ordinal) <
            cleanup.IndexOf("$Collector.WaitForExitAsync", StringComparison.Ordinal));
        Assert.Contains("$failures.Add($_.Exception)", cleanup, StringComparison.Ordinal);
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

    private static Dictionary<string, string> ReadFunctionClosure(
        string source,
        string rootFunction)
    {
        var functionNames = Regex.Matches(
                source,
                @"(?m)^function\s+(?<name>[A-Za-z0-9-]+)\s*\{")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        var closure = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(rootFunction);

        while (pending.Count > 0)
        {
            var functionName = pending.Dequeue();
            if (closure.ContainsKey(functionName))
            {
                continue;
            }

            var body = ReadFunction(source, functionName);
            closure.Add(functionName, body);
            foreach (var candidate in functionNames)
            {
                if (!closure.ContainsKey(candidate) &&
                    Regex.IsMatch(
                        body,
                        $@"(?<![A-Za-z0-9-]){Regex.Escape(candidate)}(?![A-Za-z0-9-])"))
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return closure;
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
