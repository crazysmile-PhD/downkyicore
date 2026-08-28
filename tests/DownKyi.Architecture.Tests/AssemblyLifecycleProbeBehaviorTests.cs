using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleProbeBehaviorTests
{
    private const string MutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_LEASE";
    private const string HelperAuthorityMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_HELPER_AUTHORITY";
    private const string CaptureBudgetMutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_FORENSICS_CAPTURE_BUDGET";
    private const string CaptureBudgetSelfTestRejection =
        "Forensics collector capture-window self-test did not fail closed.";
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
        Assert.Contains(
            "$null = $collector.WaitForExitAsync($CancellationToken)",
            collector,
            StringComparison.Ordinal);
        Assert.Contains("$outputTasks.WaitAsync(", collector, StringComparison.Ordinal);
        Assert.Contains("$null = $outputTasks.WaitAsync(", collector, StringComparison.Ordinal);
        Assert.Contains("$CancellationToken", collector, StringComparison.Ordinal);
        Assert.Contains("$Collector.Kill($true)", cleanup, StringComparison.Ordinal);
        Assert.Contains(
            "$Collector.WaitForExitAsync([Threading.CancellationToken]::None)",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains(
            "$null = $Collector.WaitForExitAsync([Threading.CancellationToken]::None)",
            cleanup,
            StringComparison.Ordinal);
        Assert.True(
            cleanup.IndexOf("$Collector.Kill($true)", StringComparison.Ordinal) <
            cleanup.IndexOf("$Collector.WaitForExitAsync", StringComparison.Ordinal));
        Assert.Contains("$Failures.Add($_.Exception)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void ForensicsCollectorConsumesAnOwnerAllocatedCaptureWindow()
    {
        var source = ReadLifecycleGate();
        var isolatedProcess = ReadFunction(source, "Invoke-IsolatedProcess");
        var allocator = ReadFunction(source, "New-OwnerAllocatedForensicsCaptureWindow");
        var collector = ReadFunction(source, "Invoke-BoundedForensicsCollector");
        Assert.True(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(1, CaptureBudgetSelfTestRejection)));
        Assert.False(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(1, "Unrelated lifecycle failure.")));
        Assert.False(IsExpectedCaptureBudgetMutationRejection(
            new BoundedProcessResult(0, CaptureBudgetSelfTestRejection)));
        if (string.Equals(
                Environment.GetEnvironmentVariable(CaptureBudgetMutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            BoundedProcessResult mutation;
            try
            {
                mutation = ExecuteCaptureBudgetMutation();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;
            }
            catch (AggregateException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // An unavailable artifact, platform failure, or subprocess timeout
                // is not proof that the behavioral self-test rejected the mutation.
                return;
            }

            var expectedRejection = IsExpectedCaptureBudgetMutationRejection(mutation);
            Assert.False(
                expectedRejection,
                "The real lifecycle self-test rejected the broken whole-budget helper.");
            return;
        }

        var captureWait = ReadFunction(source, "Get-ForensicsCaptureWaitMilliseconds");
        var observerClosure = ReadFunctionClosure(source, "Invoke-ForensicsObserverCapture");

        Assert.Contains(
            "$forensicsCaptureWindowMilliseconds = 15000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsCaptureCleanupWindowMilliseconds = $processCleanupGraceSeconds * 1000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-OwnerAllocatedForensicsCaptureWindow",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Count(source, "New-OwnerAllocatedForensicsCaptureWindow"));
        Assert.DoesNotContain(
            "New-OwnerAllocatedForensicsCaptureWindow",
            observerClosure.Keys);
        Assert.DoesNotContain("OwnedProcessLease", allocator, StringComparison.Ordinal);
        Assert.DoesNotContain("TransitionBudget]::Start", allocator, StringComparison.Ordinal);
        Assert.Contains(
            "[System.Diagnostics.Stopwatch]::StartNew()",
            allocator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[DateTimeOffset]::UtcNow", allocator, StringComparison.Ordinal);

        Assert.Contains("[object]$CaptureWindow", collector, StringComparison.Ordinal);
        Assert.Contains(
            "Get-ForensicsCaptureWaitMilliseconds",
            collector,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-TransitionBudgetWaitMilliseconds",
            collector,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-ForensicsTimeoutException -Exception $_.Exception",
            collector,
            StringComparison.Ordinal);

        Assert.Contains("$Budget.RemainingOperation", captureWait, StringComparison.Ordinal);
        Assert.Contains("$Budget.RemainingCleanup", captureWait, StringComparison.Ordinal);
        Assert.Contains(
            "$CaptureWindow.operationDuration",
            captureWait,
            StringComparison.Ordinal);
        Assert.Contains(
            "$CaptureWindow.cleanupDuration",
            captureWait,
            StringComparison.Ordinal);
        Assert.Contains(
            "$CaptureWindow.stopwatch.Elapsed",
            captureWait,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[DateTimeOffset]::UtcNow", captureWait, StringComparison.Ordinal);
        foreach (var function in observerClosure)
        {
            Assert.DoesNotContain(
                "[System.Diagnostics.Stopwatch]::StartNew()",
                function.Value,
                StringComparison.Ordinal);
        }

        Assert.Contains("$timeoutFailure", collector, StringComparison.Ordinal);
        Assert.Contains("$failures.Add($timeoutFailure)", collector, StringComparison.Ordinal);
        Assert.Contains(
            "Test-ForensicsCollectorCaptureWindow",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--block-forever",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsCollectorCaptureWindowSelfTestPassed",
            source,
            StringComparison.Ordinal);
    }

    private static bool IsExpectedCaptureBudgetMutationRejection(
        BoundedProcessResult mutation)
    {
        return mutation.ExitCode != 0 && mutation.Output.Contains(
            CaptureBudgetSelfTestRejection,
            StringComparison.Ordinal);
    }

    private static BoundedProcessResult ExecuteCaptureBudgetMutation()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-forensics-capture-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(
                RepositoryRoot,
                "script",
                "test-assembly-lifecycle.ps1"));
            startInfo.ArgumentList.Add("-Configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-Profile");
            startInfo.ArgumentList.Add("Local");
            startInfo.ArgumentList.Add("-Iterations");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-AssemblyPattern");
            startInfo.ArgumentList.Add("DownKyi.Core.Tests");
            startInfo.ArgumentList.Add("-ResultsDirectory");
            startInfo.ArgumentList.Add(resultsDirectory);
            startInfo.ArgumentList.Add("-ValidateForensics");
            startInfo.ArgumentList.Add("-NoBuild");
            startInfo.Environment[CaptureBudgetMutationEnvironmentVariable] = "1";

            return BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken,
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void EvidenceCaptureLeadStartsAfterTheOwnedLeaseIsEstablished()
    {
        var isolatedProcess = ReadFunction(ReadLifecycleGate(), "Invoke-IsolatedProcess");
        var leaseReadyIndex = isolatedProcess.IndexOf(
            "$processId = $lease.TargetProcessId",
            StringComparison.Ordinal);
        var observationClockIndex = isolatedProcess.IndexOf(
            "$evidenceObservationStopwatch = [System.Diagnostics.Stopwatch]::StartNew()",
            StringComparison.Ordinal);

        Assert.True(leaseReadyIndex >= 0);
        Assert.True(observationClockIndex > leaseReadyIndex);
        Assert.Contains(
            "$evidenceObservationStopwatch.Elapsed.TotalSeconds",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$stopwatch.Elapsed.TotalSeconds -ge $evidenceCaptureThresholdSeconds",
            isolatedProcess,
            StringComparison.Ordinal);
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
