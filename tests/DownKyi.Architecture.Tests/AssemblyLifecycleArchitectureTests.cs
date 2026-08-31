using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    public void DesktopSmokeTestsClearOnlyTheirOwnedSqlitePools()
    {
        var source = Read("tests/DownKyi.Desktop.Tests/UiSmokeTests.cs");
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var globalCleanupCall = string.Concat("SqliteConnection.", "ClearAllPools()");

        Assert.DoesNotContain(
            globalCleanupCall,
            source,
            StringComparison.Ordinal);
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
    public void GlobalSqlitePoolCleanupRequiresExplicitProcessOwnership()
    {
        var globalCleanupCall = string.Concat("SqliteConnection.", "ClearAllPools()");
        string[] allowedProcessOwners =
        [
            "benchmarks/DownKyi.SystemBenchmarks/Program.cs"
        ];
        var actualOwners = Directory
            .EnumerateFiles(RepositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Where(path => File.ReadAllText(path).Contains(
                globalCleanupCall,
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path)
                .Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowedProcessOwners, actualOwners);
    }

    [Fact]
    public void LifecycleGateMeasuresEveryProcessStageAndCapturesForensics()
    {
        var entrypoint = Read("script/test-assembly-lifecycle.ps1");
        var source = ReadLifecycleSources();
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
            "Get-ProcessTree",
            "dotnet-stack",
            "managed-stack.txt",
            "DOWNKYI_LIFECYCLE_MARKER",
            "stdoutPolluted",
            "stderrPolluted",
            "residualChildCount",
            "residualChildren",
            "residualChildEvidence",
            "residualChildEvidenceStatus",
            "residualChildEvidenceErrorType",
            "residualChildEvidenceCapturedCount",
            "residualChildEvidenceMissingCount",
            "residualChildQuiescenceMilliseconds",
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
            "forensicsSelfTestCaptureLeadValidated",
            "diagnosticCaptureDurationMs",
            "processExitedAtUnixMs",
            "markerReadContentionCount",
            "markerReadRetriesExhaustedCount",
            "markerReadErrorCount",
            "markerReadErrorType",
            "markerReaderSelfTestPassed",
            "markerReaderSelfTest",
            "RedirectStandardInput = $true",
            "$process.StandardInput.Close()",
            "Test-XunitReporterContractMutation",
            "reporterContractSelfTestPassed",
            "[System.IO.FileShare]::ReadWrite",
            "ValidateForensics"
        ];

        foreach (var token in requiredTokens)
        {
            Assert.Contains(token, source, StringComparison.Ordinal);
        }

        Assert.Contains("-Phase \"execution\"", entrypoint, StringComparison.Ordinal);
        Assert.Contains("-LifecycleMarkerPath $selfTestMarker", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[string]::IsNullOrWhiteSpace($LifecycleMarkerPath) -and",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$slowEvidenceCaptureLeadMilliseconds = 1000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$EvidenceThresholdSeconds - ($slowEvidenceCaptureLeadMilliseconds / 1000)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EvidenceThresholdSeconds 1.25",
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
        AssertUsesSynchronousAutomatedReporting(entrypoint, "assembly-info");
        AssertUsesSynchronousAutomatedReporting(entrypoint, "discovery");
        AssertUsesSynchronousAutomatedReporting(entrypoint, "execution");
    }

    [Fact]
    public void LifecycleEntrypointDelegatesDistinctResponsibilities()
    {
        var entrypoint = Read("script/test-assembly-lifecycle.ps1");
        var forensics = Read("script/assembly-lifecycle/forensics.ps1");
        var processExecution = Read("script/assembly-lifecycle/process-execution.ps1");
        var resultClassification = Read("script/assembly-lifecycle/result-classification.ps1");
        var reportRendering = Read("script/assembly-lifecycle/report-rendering.ps1");

        string[] componentFiles =
        [
            "forensics.ps1",
            "process-execution.ps1",
            "result-classification.ps1",
            "report-rendering.ps1"
        ];
        foreach (var componentFile in componentFiles)
        {
            Assert.Contains(
                $"Join-Path $componentRoot \"{componentFile}\"",
                entrypoint,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("function ", entrypoint, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-AssemblyLifecycleForensicsSelfTests",
            entrypoint,
            StringComparison.Ordinal);
        string[] forbiddenEntrypointMechanics =
        [
            "Gate.Forensics",
            "Gate.ResidualChild",
            "Gate.TransientChild",
            "Gate.MarkerReader",
            "--hold-after-unload-ms",
            "--spawn-residual-child-ms",
            "[System.IO.FileShare]::None",
            "$residualChildSelfTest.passed =",
            "$markerReaderSelfTest.contractChecks",
            "failureType ="
        ];
        foreach (var mechanic in forbiddenEntrypointMechanics)
        {
            Assert.DoesNotContain(mechanic, entrypoint, StringComparison.Ordinal);
        }

        Assert.Contains("function Save-ProcessEvidence", forensics, StringComparison.Ordinal);
        Assert.Contains(
            "function Invoke-AssemblyLifecycleForensicsSelfTests",
            forensics,
            StringComparison.Ordinal);
        Assert.Contains("--hold-after-unload-ms", forensics, StringComparison.Ordinal);
        Assert.Contains("--spawn-residual-child-ms", forensics, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileShare]::None", forensics, StringComparison.Ordinal);
        Assert.Contains("function Invoke-IsolatedProcess", processExecution, StringComparison.Ordinal);
        Assert.Contains("function New-ProcessPhaseResult", resultClassification, StringComparison.Ordinal);
        Assert.Contains(
            "function Complete-ResidualChildSelfTestClassification",
            resultClassification,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Complete-MarkerReaderSelfTestClassification",
            resultClassification,
            StringComparison.Ordinal);
        Assert.Contains("function New-AssemblyLifecycleReport", resultClassification, StringComparison.Ordinal);
        Assert.Contains("function Write-AssemblyLifecycleReport", reportRendering, StringComparison.Ordinal);

        var lifecycleSources = ReadLifecycleSources();
        string[] authoritativeFunctions =
        [
            "Save-ProcessEvidence",
            "Invoke-IsolatedProcess",
            "Invoke-AssemblyLifecycleForensicsSelfTests",
            "New-ProcessPhaseResult",
            "Complete-ResidualChildSelfTestClassification",
            "Complete-MarkerReaderSelfTestClassification",
            "New-AssemblyLifecycleReport",
            "Write-AssemblyLifecycleReport"
        ];
        foreach (var functionName in authoritativeFunctions)
        {
            var declaration = $"function {functionName} {{";
            var firstDeclaration = lifecycleSources.IndexOf(
                declaration,
                StringComparison.Ordinal);
            Assert.True(firstDeclaration >= 0, $"Missing lifecycle function: {functionName}");
            Assert.Equal(
                firstDeclaration,
                lifecycleSources.LastIndexOf(declaration, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void LifecycleSeamValidationHasCanonicalEntrypoint()
    {
        var validation = Read("script/test-assembly-lifecycle-seams.ps1");

        Assert.Contains("Invoke-DownKyiTestProject", validation, StringComparison.Ordinal);
        Assert.Contains(
            "Language.Parser]::ParseFile",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Complete-MarkerReaderSelfTestClassification",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "DownKyi.Architecture.Tests.AssemblyLifecycleArchitectureTests",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssemblyLifecycleArchitectureTests.trx",
            validation,
            StringComparison.Ordinal);
        Assert.Contains("if ($result.ExitCode -ne 0)", validation, StringComparison.Ordinal);
        Assert.Contains(
            "throw \"Assembly lifecycle seam validation failed",
            validation,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleAlarmParityContractsExecuteAtThePowerShellBoundary()
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-lifecycle-alarm-parity-{Guid.NewGuid():N}");
        Process? process = null;
        var cleanupFailures = new List<Exception>();
        var processReaped = true;
        var primaryFailure = await Record.ExceptionAsync(async () =>
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
            foreach (var argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-NonInteractive",
                         "-File",
                         Path.Combine(RepositoryRoot, "script", "test-assembly-lifecycle-seams.ps1"),
                         "-Configuration",
                         "Release",
                         "-BehaviorOnly",
                         "-ResultsDirectory",
                         resultsDirectory
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            var output = await standardOutput.ConfigureAwait(true);
            var error = await standardError.ConfigureAwait(true);
            Assert.True(
                process.ExitCode == 0,
                $"Lifecycle alarm parity failed. stdout={output} stderr={error}");

            var summaryPath = Path.Combine(
                resultsDirectory,
                "lifecycle-alarm-contracts.json");
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(
                summaryPath,
                TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(11, summary.RootElement.GetProperty("passed").GetInt32());
            Assert.Equal(0, summary.RootElement.GetProperty("failed").GetInt32());
        }).ConfigureAwait(true);

        if (process is not null)
        {
            var processCleanupFailure = await TerminateFixtureProcessTreeAsync(
                process).ConfigureAwait(true);
            if (processCleanupFailure is not null)
            {
                processReaped = false;
                cleanupFailures.Add(processCleanupFailure);
            }

            var disposeFailure = Record.Exception(process.Dispose);
            if (disposeFailure is not null)
            {
                cleanupFailures.Add(disposeFailure);
            }
        }

        var directoryCleanupFailure = processReaped ? Record.Exception(() =>
        {
            if (Directory.Exists(resultsDirectory))
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
        }) : null;
        if (directoryCleanupFailure is not null)
        {
            cleanupFailures.Add(directoryCleanupFailure);
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Lifecycle alarm parity failed and cleanup also failed.",
                    new[] { primaryFailure }.Concat(cleanupFailures));
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "Lifecycle alarm parity cleanup failed.",
                cleanupFailures);
        }
    }

    private static async Task<Exception?> TerminateFixtureProcessTreeAsync(Process process)
    {
        return await Record.ExceptionAsync(async () =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var reapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(reapTimeout.Token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [Fact]
    public void ResidualChildForensicsPreserveIdentityAndFailClosed()
    {
        var gate = ReadLifecycleSources();
        var probe = Read("tools/DownKyi.AssemblyLifecycleProbe/Program.cs");
        string[] requiredGateContract =
        [
            "Protect-ProcessDiagnosticText",
            "Save-ResidualChildEvidence",
            "Wait-ResidualProcessTree",
            "residual-children.json",
            "failureType -eq \"ResidualChildProcess\"",
            "residualChildSelfTestPassed",
            "residualChildSelfTest",
            "childObserved",
            "identityCaptured",
            "evidenceManifestWritten",
            "failureClassified",
            "transientChildObserved",
            "transientChildDrained",
            "transientPhasePassed",
            "cleanupCompleted",
            "redactionValidated",
            "$residualChildSelfTestContractPassed",
            "$residualChildSelfTestComplete",
            "$residualChildQuiescenceMilliseconds = 500",
            "$residualChildPollMilliseconds = 25",
            "\"--spawn-residual-child-ms\"",
            "$childProcess.Kill($true)",
            "$childProcess.WaitForExit(5000)"
        ];

        foreach (var token in requiredGateContract)
        {
            Assert.Contains(token, gate, StringComparison.Ordinal);
        }

        Assert.Contains("--spawn-residual-child-ms", probe, StringComparison.Ordinal);
        Assert.Contains("--child-hold-ms", probe, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("conhost", gate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processName -eq", gate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "residualChildCount -eq 0 -or",
            gate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "residualChildEvidenceStatus -eq \"captured\" -or",
            gate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsMarkerReaderSelfTestIsDetailedAndFailsClosed()
    {
        var source = ReadLifecycleSources();
        string[] requiredContract =
        [
            "New-MarkerReaderSelfTestState",
            "-Required $markerReaderSelfTestRequired",
            "required = $Required",
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

    private static string ReadLifecycleSources()
    {
        return string.Join(
            '\n',
            Read("script/test-assembly-lifecycle.ps1"),
            Read("script/assembly-lifecycle/forensics.ps1"),
            Read("script/assembly-lifecycle/process-execution.ps1"),
            Read("script/assembly-lifecycle/result-classification.ps1"),
            Read("script/assembly-lifecycle/report-rendering.ps1"));
    }

    private static bool IsGeneratedPath(string path)
    {
        var relativePath = Path.GetRelativePath(RepositoryRoot, path);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ".git");
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
