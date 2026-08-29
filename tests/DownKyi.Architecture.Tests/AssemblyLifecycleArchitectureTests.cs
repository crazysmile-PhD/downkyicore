using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void EveryTestAssemblyUsesFixtureOwnedDataIsolation()
    {
        var testProps = Read("tests/Directory.Build.props");
        var registration = Read("tests/TestInfrastructure/TestDataIsolationRegistration.cs");
        var fixture = Read("tests/TestInfrastructure/TestDataIsolation.cs");

        Assert.Contains("DownKyi.TestInfrastructure.csproj", testProps, StringComparison.Ordinal);
        Assert.Contains("TestDataIsolationRegistration.cs", testProps, StringComparison.Ordinal);
        Assert.Contains("Xunit.AssemblyFixture", registration, StringComparison.Ordinal);
        Assert.Contains("IAsyncDisposable", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain(
            string.Concat("Module", "Initializer"),
            fixture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            string.Concat("Process", "Exit"),
            fixture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleMarkerOwnershipIsConsumedBeforeNestedTestProcessesStart()
    {
        var lifecycle = Read("script/test-assembly-lifecycle.ps1");
        var guard = Read("tests/CentralTestExecutionGuard.cs");

        Assert.Contains(
            "DOWNKYI_LIFECYCLE_MARKER_OWNER = \"1\"",
            lifecycle,
            StringComparison.Ordinal);
        var consumeIndex = guard.IndexOf(
            "ConsumeLifecycleMarkerOwnership();",
            StringComparison.Ordinal);
        var guardBypassIndex = guard.IndexOf(
            "DOWNKYI_TEST_MUTATE_CENTRAL_GUARD_BYPASS",
            StringComparison.Ordinal);
        Assert.True(consumeIndex >= 0 && consumeIndex < guardBypassIndex);
        Assert.Contains(
            "Environment.SetEnvironmentVariable(LifecycleMarkerOwnerEnvironmentVariable, null)",
            guard,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment.SetEnvironmentVariable(LifecycleMarkerEnvironmentVariable, null)",
            guard,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSmokeTestsClearOnlyTheirOwnedSqlitePools()
    {
        var source = Read("tests/DownKyi.Desktop.Tests/UiSmokeTests.cs");
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            3,
            Regex.Count(
                normalizedSource,
                @"(?m)^\s+ClearOwnedSqlitePool\(databasePath\);$",
                RegexOptions.CultureInvariant));
        Assert.Contains(
            "DataSource = databasePath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mode = SqliteOpenMode.ReadWriteCreate",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Pooling = true", source, StringComparison.Ordinal);
        Assert.Contains("DefaultTimeout = 5", source, StringComparison.Ordinal);
        Assert.Contains(
            "SqliteConnection.ClearPool(connection)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleGateMeasuresEveryProcessStageAndCapturesForensics()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");
        string[] requiredTokens =
        [
            "\"load\"",
            "\"assembly-info\"",
            "\"discovery\"",
            "\"execution\"",
            "\"assembly-teardown\"",
            "\"process-exit\"",
            "P50",
            "P95",
            "P99",
            "WaitReason",
            "Get-DiagnosticProcessTreeSnapshot",
            "OwnedProcessLease]::StartAsync",
            "OwnedDiagnosticCollector]::CollectAsync",
            "DiagnosticCollectorRequest]::new",
            "TransitionBudget]::Start",
            "AllocateDiagnosticCollectorWindow",
            "Invoke-OwnedDiagnosticCollector",
            "ownedTreeQuiescent",
            "ownedProcessCleanupFailures",
            "EvidenceHoldRequest",
            "CompleteEvidenceHoldAsync",
            "EvidenceCaptureCompletion",
            "Invoke-ForensicsObserverCapture",
            "processFailureType",
            "forensicsFailureType",
            "dotnet-stack",
            "managed-stack.txt",
            "DOWNKYI_LIFECYCLE_MARKER",
            "DOWNKYI_LIFECYCLE_MARKER_OWNER",
            "stdoutPolluted",
            "stderrPolluted",
            "residualChildCount",
            "residualChildren",
            "residualChildEvidence",
            "residualChildEvidenceStatus",
            "residualChildEvidenceErrorType",
            "residualChildEvidenceCapturedCount",
            "residualChildEvidenceMissingCount",
            "transientChildCount",
            "transientChildren",
            "transientChildObservedCount",
            "failureType",
            "errorType",
            "workingTreeDirty",
            "slowEvidenceStatus",
            "slowEvidenceComplete",
            "slowEvidenceMissingCount",
            "slowEvidenceCaptureLeadMilliseconds",
            "slowEvidenceTriggeredBeforeThreshold",
            "slowEvidenceCaptureArmedAfterMilliseconds",
            "slowEvidenceCaptureCompletedAtUnixMilliseconds",
            "slowEvidenceCaptureCompletedBeforeTargetExit",
            "forensicsSelfTestCaptureLeadValidated",
            "forensicsSelfTestPositiveCaptureThresholdValidated",
            "forensicsSelfTestObservedCaptureThresholdSeconds",
            "forensicsSelfTestCaptureCompletedBeforeTargetExitValidated",
            "forensicsSelfTestEvidenceHoldValidated",
            "forensicsSelfTestReleaseOrderingMutationValidated",
            "forensicsSelfTestReleaseOrderingMutation",
            "slowEvidenceOrderingSelfTestRequired",
            "slowEvidenceOrderingSelfTestPassed",
            "slowEvidenceOrderingSelfTest",
            "forensicsCaptureWindowMilliseconds",
            "forensicsCaptureCleanupWindowMilliseconds",
            "forensicsCollectorCaptureWindowSelfTestRequired",
            "forensicsCollectorCaptureWindowSelfTestPassed",
            "diagnosticCaptureDurationMs",
            "processExitedAtUnixMs",
            "TargetExitedAtUnixMilliseconds",
            "markerReadContentionCount",
            "markerReadRetriesExhaustedCount",
            "markerReadErrorCount",
            "markerReadErrorType",
            "markerReaderSelfTestPassed",
            "markerReaderSelfTest",
            "processLeaseSelfTestPassed",
            "Test-XunitReporterContractMutation",
            "reporterContractSelfTestPassed",
            "[System.IO.FileShare]::ReadWrite",
            "ValidateForensics"
        ];

        foreach (var token in requiredTokens)
        {
            Assert.Contains(token, source, StringComparison.Ordinal);
        }

        Assert.Contains("-Phase \"execution\"", source, StringComparison.Ordinal);
        Assert.Contains("-LifecycleMarkerPath $selfTestMarker", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[string]::IsNullOrWhiteSpace($LifecycleMarkerPath) -and",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$hostedCollectorStartupAllowanceMilliseconds = 3000",
            source,
            StringComparison.Ordinal);
        Assert.Matches(
            @"\$slowEvidenceCaptureLeadMilliseconds\s*=\s*" +
            @"\$hostedCollectorStartupAllowanceMilliseconds",
            source);
        Assert.Contains(
            "$EvidenceThresholdSeconds - ($EvidenceCaptureLeadMilliseconds / 1000)",
            source,
            StringComparison.Ordinal);
        Assert.Matches(@"\[Math\]::Max\(\s*0\.0,", source);
        Assert.Contains(
            "-EvidenceThresholdSeconds $forensicsSelfTestEvidenceThresholdSeconds",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$selfTest.slowEvidenceTriggeredBeforeThreshold",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsSelfTestCaptureLeadValidated =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsCaptureWindowMilliseconds = 15000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Test-SlowEvidenceCaptureOrdering",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--exit-after-delay-with-ready",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EvidenceCaptureLeadMilliseconds 1000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EvidenceCaptureLeadMilliseconds 5000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$immediateDispatchMutation.slowEvidenceCaptureArmedAfterMilliseconds -lt 500",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-InjectedPostCaptureDelayMilliseconds",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$slowCompletionMutation.slowEvidenceCaptureCompletedAfterMilliseconds -ge",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$slowCompletionMutationPhase.success",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$slowCompletionUtcOffsetMilliseconds = -60000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-InjectedCaptureCompletionUtcOffsetMilliseconds",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "slowCompletionWallClockOrderingDiverged =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$slowCompletionMutation.slowEvidenceCaptureCompletedAtUnixMilliseconds -lt",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "($OperationTimeoutSeconds * 1000.0) -",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$budget.RemainingOperation.TotalMilliseconds",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$selfTest.slowEvidenceCaptureCompletedAfterMilliseconds -lt",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item -LiteralPath $readyPath -Force -ErrorAction Stop",
            source,
            StringComparison.Ordinal);
        Assert.Contains("readyFilesCleaned =", source, StringComparison.Ordinal);
        Assert.Contains(
            "$mutationPhase.failureType -eq \"SlowEvidenceMissing\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$selfTest.evidenceCaptureThresholdSeconds -",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[Math]::Abs(", source, StringComparison.Ordinal);
        Assert.Contains(
            "$selfTest.slowEvidenceCaptureArmedAfterMilliseconds / 1000",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"\$forensicsSelfTestObservedCaptureThresholdSeconds\s*=\s*" +
            @"\$selfTest\.evidenceCaptureThresholdSeconds",
            source);
        var captureCompletionIndex = source.IndexOf(
            "$slowEvidenceCaptureCompletedAfterMilliseconds = [Math]::Round(",
            StringComparison.Ordinal);
        var evidenceHoldReleaseIndex = source.IndexOf(
            "$lease.CompleteEvidenceHoldAsync(",
            captureCompletionIndex,
            StringComparison.Ordinal);
        Assert.True(captureCompletionIndex >= 0);
        Assert.True(evidenceHoldReleaseIndex > captureCompletionIndex);
        Assert.Contains(
            "-InjectCaptureCompletionAfterEvidenceHoldRelease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$targetExitWaitHandle = $lease.TargetExitedToken.WaitHandle",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$targetExitWaitHandle.WaitOne($budget.RemainingOperation)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$forensicsSelfTestReleaseOrderingMutationValidated =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-not $releaseOrderingMutation." +
            "slowEvidenceCaptureCompletedBeforeTargetExit",
            source,
            StringComparison.Ordinal);
        AssertUsesSynchronousAutomatedReporting(source, "assembly-info");
        AssertUsesSynchronousAutomatedReporting(source, "discovery");
        AssertUsesSynchronousAutomatedReporting(source, "execution");
    }

    [Fact]
    public void SlowEvidenceSelfTestPreservesCausalTransitionsAndNullCleanupBoundary()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");
        string[] requiredTokens =
        [
            "function ConvertTo-ExceptionEvidence",
            "function Test-LifecycleFailureEvidenceSeparation",
            "function Invoke-SlowEvidenceOrderingScenario",
            "targetStart = $null",
            "readyEstablishment = $null",
            "collectorArm = $null",
            "targetExit = $null",
            "collectorCompletion = $null",
            "cleanupCompletion = $null",
            "faultBoundary = $null",
            "firstFailedScenarioTransition =",
            "scenarioTransitionsRecorded =",
            "failureSeparationPreserved =",
            "diagnosticProcessIdentities ="
        ];
        foreach (var token in requiredTokens)
        {
            Assert.Contains(token, source, StringComparison.Ordinal);
        }

        Assert.Matches(
            @"if \(\$null -ne \$authorization\)\s*\{\s*" +
            @"Close-DownKyiTestProcessAuthorization -Authorization \$authorization",
            source);
        var selfTestStart = source.IndexOf(
            "function Test-SlowEvidenceCaptureOrdering",
            StringComparison.Ordinal);
        var contractStart = source.IndexOf(
            "$contractChecks = [ordered]@{",
            selfTestStart,
            StringComparison.Ordinal);
        var contractEnd = source.IndexOf(
            "$passed =",
            contractStart,
            StringComparison.Ordinal);
        Assert.True(selfTestStart >= 0);
        Assert.True(contractStart > selfTestStart);
        Assert.True(contractEnd > contractStart);
        Assert.DoesNotContain(
            ".ProcessId",
            source[contractStart..contractEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecyclePhaseProcessCorrectnessHasOneLeaseOwner()
    {
        var gate = Read("script/test-assembly-lifecycle.ps1");

        Assert.Contains(
            "[DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync",
            gate,
            StringComparison.Ordinal);
        Assert.Contains(
            "[DownKyi.ProcessSupervision.TransitionBudget]::Start",
            gate,
            StringComparison.Ordinal);
        Assert.Contains(
            "[DownKyi.ProcessSupervision.OwnedDiagnosticCollector]::CollectAsync",
            gate,
            StringComparison.Ordinal);
        Assert.Contains("AllocateDiagnosticCollectorWindow", gate, StringComparison.Ordinal);
        Assert.Contains("Invoke-OwnedDiagnosticCollector", gate, StringComparison.Ordinal);
        Assert.Contains("ownedTreeQuiescent", gate, StringComparison.Ordinal);
        Assert.Contains("ownedProcessCleanupFailures", gate, StringComparison.Ordinal);
        Assert.Contains("Get-DiagnosticProcessTreeSnapshot", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Get-ProcessTree", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-ResidualProcessTree", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ProcessIdentityKey", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-LiveObservedProcess", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-DownKyiOwnedProcess", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseObservedChildren", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedChildReleaseLease", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Invoke-BoundedForensicsCollector", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Stop-BoundedForensicsCollector", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function New-OwnerAllocatedForensicsCaptureWindow", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function New-EvidenceCaptureLease", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Start-EvidenceCaptureLease", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Complete-EvidenceCaptureLease", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("function Close-EvidenceCaptureLease", gate, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$processExitedAtUnixMs = [DateTimeOffset]::UtcNow",
            gate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsMarkerReaderSelfTestIsDetailedAndFailsClosed()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");
        string[] requiredContract =
        [
            "required = $markerReaderSelfTestRequired",
            "executed = $false",
            "passed = $false",
            "contentionObserved = $false",
            "contentionCount = 0",
            "recoveredAfterLockRelease = $false",
            "markerParsedAfterRecovery = $false",
            "errorType = $null",
            "$markerReaderSelfTestContractPassed",
            "$SelfTest.contentionCount -gt 0",
            "$null -eq $SelfTest.errorType",
            "success = $markerReaderSelfTestComplete",
            "errorType = $markerReaderSelfTestFailureType",
            "validProofAccepted",
            "errorTypeRejected",
            "zeroContentionRejected",
            "incompleteProofRejected",
            "errorClassificationPassed",
            "Get-LifecycleMarkerReadFailureCategory",
            "Formal Windows lifecycle profiles require -ValidateForensics.",
            "marker-reader-self-test"
        ];

        foreach (var token in requiredContract)
        {
            Assert.Contains(token, source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "-not $markerReaderSelfTest.required -or",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "($markerReaderSelfTest.executed -and",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.Split("failureType =", StringSplitOptions.None).Length - 1 >= 5,
            "Every synthetic and process phase family must expose failureType.");
    }

    [Fact]
    public void LifecycleProfilesRemainConfiguredForNormalRelease()
    {
        var lifecycle = Read("script/test-assembly-lifecycle.ps1");
        var quality = Read(".github/workflows/quality.yml");
        var release = Read(".github/workflows/build.yml");
        var activeReleaseContract = Read(
            "docs/product-specs/v1.1.1-corrective-release-gate.md");
        var expectedProfiles = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Local"] = 1,
            ["PR"] = 3,
            ["Main"] = 5,
            ["Rehearsal"] = 100,
            ["Flaky"] = 500
        };
        var actualProfiles = Regex.Matches(
                lifecycle,
                @"(?m)^\s+(Local|PR|Main|Rehearsal|Flaky) = ([0-9]+)\s*$",
                RegexOptions.CultureInvariant)
            .ToDictionary(
                match => match.Groups[1].Value,
                match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

        Assert.Equal(expectedProfiles, actualProfiles);
        Assert.Contains(
            "profiles execute 3, 5 and 100 iterations",
            activeReleaseContract.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n  ", " ", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "profiles execute 3, 50 and 100 iterations",
            activeReleaseContract.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n  ", " ", StringComparison.Ordinal),
            StringComparison.Ordinal);

        Assert.Contains("assembly-lifecycle:", quality, StringComparison.Ordinal);
        Assert.Contains("pull_request:", quality, StringComparison.Ordinal);
        Assert.Contains(
            "branches:\n      - main",
            quality.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "$profile = if (\"${{ github.event_name }}\" -eq \"pull_request\") { \"PR\" } else { \"Main\" }",
            quality,
            StringComparison.Ordinal);
        Assert.Contains("-ValidateForensics", quality, StringComparison.Ordinal);
        Assert.Contains("dotnet-stack", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("-Profile Rehearsal", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("-Profile Flaky", quality, StringComparison.Ordinal);

        Assert.Contains("assembly-lifecycle-release:", release, StringComparison.Ordinal);
        Assert.Contains("-Profile Rehearsal", release, StringComparison.Ordinal);
        Assert.Contains("-ValidateForensics", release, StringComparison.Ordinal);
        Assert.Contains(
            "needs.assembly-lifecycle-release.result == 'success'",
            release,
            StringComparison.Ordinal);
        Assert.DoesNotContain("refs/tags/v1.1.2", release, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalVerificationCannotOmitLifecycleOwnershipOrRepeatedProcessChecks()
    {
        var livePlan = Read("docs/refactoring-live-plan.md");
        var operations = Read("docs/operations/verification-and-rollback.md");
        var releaseGate = Read("docs/product-specs/v1.1.1-corrective-release-gate.md");

        Assert.Contains("audit-lifecycle-ownership.ps1", livePlan, StringComparison.Ordinal);
        Assert.Contains("-Iterations 5", livePlan, StringComparison.Ordinal);
        Assert.Contains("-ValidateForensics", livePlan, StringComparison.Ordinal);
        Assert.Contains("Gate 10 is complete only", livePlan, StringComparison.Ordinal);

        Assert.Contains("-Iterations 5", operations, StringComparison.Ordinal);
        Assert.Contains("-Profile Rehearsal", operations, StringComparison.Ordinal);
        Assert.Contains(
            "-ResultsDirectory ./artifacts/assembly-lifecycle/release",
            operations,
            StringComparison.Ordinal);

        Assert.Contains("100", releaseGate, StringComparison.Ordinal);
        Assert.Contains("machine-readable report", releaseGate, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopMainLoopAwaitsApplicationAndHostTeardown()
    {
        var desktopApplication = Read("src/DownKyi.Desktop/DesktopApplication.cs");
        var app = Read("src/DownKyi.Desktop/App.axaml.cs");
        const string desktopPrefix = "desktop.";
        const string legacyExitSubscription = "Exit += OnExit";

        Assert.Contains("finally", desktopApplication, StringComparison.Ordinal);
        Assert.Contains("await application.DisposeAsync()", desktopApplication, StringComparison.Ordinal);
        Assert.Contains("IAsyncDisposable", app, StringComparison.Ordinal);
        Assert.Contains("RequestShutdownAsync(CancellationToken.None)", app, StringComparison.Ordinal);
        Assert.Contains("DetachUnhandledExceptionLogging()", app, StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.UnhandledException -= OnUiUnhandledException",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            desktopPrefix + legacyExitSubscription,
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleOwnerPolicyDefinesActionableTeardownContracts()
    {
        var policyPath = Path.Combine(
            RepositoryRoot,
            "docs",
            "testing",
            "assembly-lifecycle-owners.json");
        using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
        var owners = document.RootElement.GetProperty("owners").EnumerateArray().ToArray();

        Assert.NotEmpty(owners);
        foreach (var owner in owners)
        {
            Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("start").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("stop").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(owner.GetProperty("teardown").GetString()));
            Assert.NotEmpty(owner.GetProperty("paths").EnumerateArray());
            Assert.NotEmpty(owner.GetProperty("allowedMechanisms").EnumerateArray());
        }
    }

    private static void AssertUsesSynchronousAutomatedReporting(string source, string phase)
    {
        var phaseMarker = $"-Phase \"{phase}\"";
        var phaseStart = source.LastIndexOf(phaseMarker, StringComparison.Ordinal);
        Assert.True(phaseStart >= 0, $"Lifecycle phase was not found: {phase}");

        var previousResult = source.LastIndexOf(
            "$phaseResults += New-ProcessPhaseResult",
            phaseStart,
            StringComparison.Ordinal);
        var guardedInvocation = source.LastIndexOf(
            "Invoke-XunitAutomatedPhase",
            phaseStart,
            StringComparison.Ordinal);
        Assert.True(
            guardedInvocation > previousResult,
            $"Lifecycle phase must use the guarded xUnit invocation: {phase}");

        var phaseEnd = source.IndexOf(
            "$phaseResults += New-ProcessPhaseResult",
            phaseStart,
            StringComparison.Ordinal);
        Assert.True(phaseEnd > phaseStart, $"Lifecycle phase result was not found: {phase}");

        var phaseInvocation = source[phaseStart..phaseEnd];
        Assert.Matches("\"-automated\",\\s*\"sync\"", phaseInvocation);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
