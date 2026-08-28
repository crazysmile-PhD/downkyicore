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
                "    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(",
                "    $startInfo = New-Object -TypeName System.Diagnostics.ProcessStartInfo\n" +
                "    $collector = New-Object -TypeName System.Diagnostics.Process\n" +
                "    $null = Start-Process -FilePath 'pwsh' -PassThru\n" +
                "    Stop-Process -Id 1\n" +
                "    Wait-Process -Id 1\n" +
                "    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(",
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
            "[AggregateException]::new",
            ".Kill(",
            ".WaitForExit(",
            ".WaitForExitAsync("
        ];

        foreach (var function in observerClosure)
        {
            foreach (var authority in forbiddenObserverAuthorities)
            {
                Assert.DoesNotContain(authority, function.Value, StringComparison.Ordinal);
            }
        }

        Assert.Contains("Save-ProcessEvidence", observerClosure.Keys);
        Assert.Contains("Get-DiagnosticProcessTreeSnapshot", observerClosure.Keys);
        Assert.Contains("Invoke-OwnedDiagnosticCollector", observerClosure.Keys);
        Assert.DoesNotContain("Invoke-BoundedForensicsCollector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-BoundedForensicsCollector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsExceptionType", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsTimeoutException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ForensicsCollectorCaptureWindow", source, StringComparison.Ordinal);

        Assert.Contains("EvidenceHoldRequest", source, StringComparison.Ordinal);
        Assert.Contains("CompleteEvidenceHoldAsync", source, StringComparison.Ordinal);
        Assert.Contains("EvidenceCaptureCompletion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function New-EvidenceCaptureLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedChildReleaseLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-ResidualProcessTree", source, StringComparison.Ordinal);

        AssertCollectorClosureAstIsClean(observerClosure);
    }

    [Fact]
    public void CompiledCollectorBoundaryOwnsStartWaitTerminateReapAndDrain()
    {
        var collector = ReadFunction(ReadLifecycleGate(), "Invoke-OwnedDiagnosticCollector");

        Assert.Contains("DiagnosticCollectorRequest]::new", collector, StringComparison.Ordinal);
        Assert.Contains("OwnedDiagnosticCollector]::CollectAsync", collector, StringComparison.Ordinal);
        Assert.Contains("$CancellationToken", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.Diagnostics.Process]", collector, StringComparison.Ordinal);
        Assert.DoesNotContain(".Start(", collector, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("List[Exception]", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("AggregateException", collector, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellReportPreservesTypedCollectorFailureEvidence()
    {
        var source = ReadLifecycleGate();
        var observer = ReadFunction(source, "Invoke-ForensicsObserverCapture");
        var isolatedProcess = ReadFunction(source, "Invoke-IsolatedProcess");
        var phaseResult = ReadFunction(source, "New-ProcessPhaseResult");

        Assert.Contains(
            "Get-DiagnosticCollectorExecutionFailure",
            observer,
            StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticCollectorExecutionException",
            ReadFunction(source, "Get-DiagnosticCollectorExecutionFailure"),
            StringComparison.Ordinal);
        Assert.Contains("collectorFailureKind", observer, StringComparison.Ordinal);
        Assert.Contains("collectorEvidence", observer, StringComparison.Ordinal);
        Assert.Contains("collectorCleanupFailures", observer, StringComparison.Ordinal);
        Assert.Contains(
            "slowEvidenceCollectorFailureKind",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "exitEvidenceCollectorFailureKind",
            isolatedProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "slowEvidenceCollectorCleanupFailures",
            phaseResult,
            StringComparison.Ordinal);
        Assert.Contains(
            "exitEvidenceCollectorCleanupFailures",
            phaseResult,
            StringComparison.Ordinal);
        Assert.Contains(
            "forensicsCollectorCaptureWindowSelfTest =",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ForensicsCollectorsConsumeCallerAllocatedTypedWindows()
    {
        var source = ReadLifecycleGate();
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
                return;
            }

            var expectedRejection = IsExpectedCaptureBudgetMutationRejection(mutation);
            Assert.False(
                expectedRejection,
                "The real lifecycle self-test rejected the broken whole-budget collector window.");
            return;
        }

        var isolatedProcess = ReadFunction(source, "Invoke-IsolatedProcess");
        var observerClosure = ReadFunctionClosure(source, "Invoke-ForensicsObserverCapture");
        var collector = ReadFunction(source, "Invoke-OwnedDiagnosticCollector");
        var delay = ReadFunction(source, "Wait-ForensicsObserverDelay");
        var snapshot = ReadFunction(source, "Get-DiagnosticProcessTreeSnapshot");

        Assert.Contains(
            "$forensicsCaptureWindowMilliseconds = 15000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsCaptureCleanupWindowMilliseconds = $processCleanupGraceSeconds * 1000",
            source,
            StringComparison.Ordinal);
        Assert.Equal(3, Regex.Count(source, "AllocateDiagnosticCollectorWindow"));
        Assert.Contains("AllocateDiagnosticCollectorWindow", isolatedProcess, StringComparison.Ordinal);
        foreach (var function in observerClosure.Values)
        {
            Assert.DoesNotContain("TransitionBudget]::Start", function, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "[System.Diagnostics.Stopwatch]::StartNew()",
                function,
                StringComparison.Ordinal);
        }
        Assert.Contains("[object]$CaptureWindow", collector, StringComparison.Ordinal);
        Assert.Contains("$CaptureWindow.DelayAsync", delay, StringComparison.Ordinal);
        Assert.Contains("$CaptureWindow.RemainingOperation", snapshot, StringComparison.Ordinal);
        Assert.Contains(
            "Test-OwnedDiagnosticCollectorCaptureWindow",
            source,
            StringComparison.Ordinal);
        Assert.Contains("--block-forever", source, StringComparison.Ordinal);
        Assert.Contains(
            CaptureBudgetMutationEnvironmentVariable,
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("New-OwnerAllocatedForensicsCaptureWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ForensicsCaptureWaitMilliseconds", source, StringComparison.Ordinal);
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

    private static void AssertCollectorClosureAstIsClean(
        IReadOnlyDictionary<string, string> closure)
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-collector-closure-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(temporaryPath, string.Join(Environment.NewLine, closure.Values));
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$Path=$env:DOWNKYI_COLLECTOR_AST_PATH; " +
                "$tokens=$null; $errors=$null; " +
                "$ast=[System.Management.Automation.Language.Parser]::ParseFile(" +
                "$Path,[ref]$tokens,[ref]$errors); " +
                "if($errors.Count -gt 0){$errors | ForEach-Object Message; exit 8}; " +
                "$legacy=@('New-OwnerAllocatedForensicsCaptureWindow'," +
                "'Get-ForensicsCaptureWaitMilliseconds','Invoke-BoundedForensicsCollector'," +
                "'Stop-BoundedForensicsCollector','Test-ForensicsCollectorCaptureWindow'); " +
                "$violations=[Collections.Generic.List[string]]::new(); " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.FunctionDefinitionAst]},$true) | " +
                "Where-Object Name -In $legacy | ForEach-Object {$violations.Add($_.Name)}; " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.InvokeMemberExpressionAst]},$true) | " +
                "ForEach-Object {$text=$_.Extent.Text; " +
                "if($text -match '(?i)\\[(System\\.)?Diagnostics\\.(ProcessStartInfo|Process)\\]" +
                "\\s*::\\s*(new|Start)' -or " +
                "$text -match '(?i)\\.(Kill|WaitForExit|WaitForExitAsync)\\s*\\(' -or " +
                "$text -match '(?i)Stopwatch\\]\\s*::\\s*StartNew' -or " +
                "$text -match '(?i)CancellationTokenSource\\]\\s*::' -or " +
                "$text -match '(?i)AggregateException\\]\\s*::\\s*new')" +
                "{$violations.Add($text)}}; " +
                "$ast.FindAll({param($n) $n -is " +
                "[System.Management.Automation.Language.CommandAst]},$true) | " +
                "ForEach-Object {$name=$_.GetCommandName(); $text=$_.Extent.Text; " +
                "if($name -match '(?i)^(Start|Stop|Wait)-Process$' -or " +
                "($name -match '(?i)^New-Object$' -and " +
                "$text -match '(?i)(System\\.)?Diagnostics\\." +
                "(ProcessStartInfo|Process)')){$violations.Add($text)}}; " +
                "if($violations.Count -gt 0){$violations; exit 9}; exit 0");
            startInfo.Environment["DOWNKYI_COLLECTOR_AST_PATH"] = temporaryPath;

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken,
                TimeSpan.FromSeconds(15));
            Assert.True(
                result.ExitCode == 0,
                $"Collector closure contains forbidden PowerShell ownership AST: {result.Output}");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
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
