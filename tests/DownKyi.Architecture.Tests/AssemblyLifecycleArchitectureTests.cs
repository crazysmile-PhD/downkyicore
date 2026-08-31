using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleArchitectureTests
{
    [Fact]
    public void LifecycleFixtureOwnsDeterministicTeardown()
    {
        var fixture = Read("tests/TestInfrastructure/TestDataIsolation.cs");
        var registration = Read("tests/TestInfrastructure/TestDataIsolationRegistration.cs");

        Assert.Contains("Xunit.AssemblyFixture", registration, StringComparison.Ordinal);
        Assert.Contains("IAsyncDisposable", fixture, StringComparison.Ordinal);
        Assert.Contains("WriteLifecycleMarker(\"started\")", fixture, StringComparison.Ordinal);
        Assert.Contains("WriteLifecycleMarker(\"disposing\")", fixture, StringComparison.Ordinal);
        Assert.Contains("WriteLifecycleMarker(\"disposed\")", fixture, StringComparison.Ordinal);
        Assert.Contains("DeleteRoot()", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleGateKeepsSixPhasesAndOneProcessOwner()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");

        foreach (var phase in new[]
                 {
                     "load",
                     "assembly-info",
                     "discovery",
                     "execution",
                     "assembly-teardown",
                     "process-exit"
                 })
        {
            Assert.Contains($"\"{phase}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "[DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Process]::Start(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".WaitForExit(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleGateUsesCompiledAuthorizationAndOneTransitionBudget()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");

        Assert.Contains(
            "[DownKyi.CentralTestRunner.CentralTestAuthorization]::Issue(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[DownKyi.ProcessSupervision.TransitionBudget]::Start(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "function New-DownKyiTestProcessAuthorization",
            Read("script/test-project-runner.ps1"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleProfilesSeparateFastFeedbackFromStress()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");
        var quality = Read(".github/workflows/quality.yml");
        var release = Read(".github/workflows/build.yml");

        var profiles = Regex.Matches(
                source,
                @"(?m)^\s+(Local|PR|Main|Rehearsal|Flaky) = ([0-9]+)\s*$")
            .ToDictionary(
                match => match.Groups[1].Value,
                match => int.Parse(
                    match.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

        Assert.Equal(1, profiles["Local"]);
        Assert.Equal(3, profiles["PR"]);
        Assert.Equal(5, profiles["Main"]);
        Assert.Equal(100, profiles["Rehearsal"]);
        Assert.Equal(500, profiles["Flaky"]);
        Assert.Contains("-Profile $profile", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("-Profile Rehearsal", quality, StringComparison.Ordinal);
        Assert.Contains("assembly-lifecycle-rehearsal:", release, StringComparison.Ordinal);
        Assert.Contains("assembly-lifecycle-release-ready", release, StringComparison.Ordinal);
        Assert.Contains("-Profile Rehearsal", release, StringComparison.Ordinal);
        Assert.DoesNotContain("assembly-lifecycle-release-standard:", release, StringComparison.Ordinal);
        Assert.DoesNotContain("shard-manifest.json", release, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureSummaryExposesTheActionableLifecycleContract()
    {
        var source = Read("script/test-assembly-lifecycle.ps1");

        foreach (var field in new[]
                 {
                     "Assembly:",
                     "Iteration:",
                     "Phase:",
                     "FailureKind:",
                     "PrimaryFailure:",
                     "TargetExitCode:",
                     "OwnedTreeQuiescent:",
                     "CleanupFailures:",
                     "Stdout:",
                     "Stderr:",
                     "EvidencePath:",
                     "LikelyOwner:"
                 })
        {
            Assert.Contains(field, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TargetExitDuringSlowCapturePreservesTypedForensicsFailure()
    {
        var repositoryRoot = GetRepositoryRoot();
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-target-exit-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(
                repositoryRoot,
                "script",
                "test-assembly-lifecycle.ps1"));
            startInfo.ArgumentList.Add("-Configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-NoBuild");
            startInfo.ArgumentList.Add("-ValidateTargetExitDuringCapture");
            startInfo.ArgumentList.Add("-ResultsDirectory");
            startInfo.ArgumentList.Add(resultsDirectory);

            var result = BoundedProcessRunner.Run(
                startInfo,
                TestContext.Current.CancellationToken,
                TimeSpan.FromSeconds(90));

            Assert.Equal(0, result.ExitCode);
            const string marker = "DOWNKYI_TARGET_EXIT_CAPTURE_RESULT=";
            var payload = Assert.Single(
                result.Output.ReplaceLineEndings("\n").Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries),
                line => line.StartsWith(marker, StringComparison.Ordinal));
            using var document = JsonDocument.Parse(payload[marker.Length..]);
            var root = document.RootElement;
            Assert.Equal("SlowEvidenceMissing", GetString(root, "failureType"));
            Assert.Equal(
                "target-exited-during-capture",
                GetString(root, "slowEvidenceStatus"));
            Assert.Equal(
                "TargetExitedDuringCapture",
                GetString(root, "slowEvidenceErrorType"));
            Assert.Equal(
                "CallerCancelled",
                GetString(root, "slowEvidenceCollectorFailureKind"));
            Assert.StartsWith(
                "TargetExitedDuringCapture:",
                GetString(root, "primaryFailure"),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "CommandNotFoundException",
                GetString(root, "slowEvidenceErrorMessage"),
                StringComparison.Ordinal);
            Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
            Assert.True(root.GetProperty("slowThresholdExceeded").GetBoolean());
            Assert.True(root.GetProperty("collectorStarted").GetBoolean());
            Assert.True(root.GetProperty("targetExitSignalObserved").GetBoolean());
            Assert.True(root.GetProperty("ownedTreeQuiescent").GetBoolean());
            Assert.Equal(0, root.GetProperty("cleanupFailureCount").GetInt32());
            var captureArmed = root.GetProperty(
                "captureArmedAfterMilliseconds").GetDouble();
            var targetExit = root.GetProperty(
                "targetExitedAfterMilliseconds").GetDouble();
            var captureCompleted = root.GetProperty(
                "captureCompletedAfterMilliseconds").GetDouble();
            Assert.True(captureArmed < targetExit);
            Assert.True(targetExit <= captureCompleted);
            Assert.True(root.GetProperty(
                "captureCompletedAfterTargetExit").GetBoolean());
            Assert.Equal(
                "DownKyi.ProcessSupervision.DiagnosticCollectorExecutionException",
                root.GetProperty("slowEvidenceExceptionEvidence")
                    .GetProperty("outer")
                    .GetProperty("type")
                    .GetString());
        }
        finally
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [Fact]
    public void ProcessOwnerKeepsAuthoritativePlatformBackends()
    {
        var containment = Read(
            "tools/DownKyi.ProcessSupervision/PlatformProcessContainment.cs");
        var linux = Read(
            "tools/DownKyi.ProcessSupervision/LinuxCgroupContainmentLease.cs");
        var mac = Read(
            "tools/DownKyi.ProcessSupervision/MacProcessGroupContainmentLease.cs");

        Assert.Contains("JobObject", containment, StringComparison.Ordinal);
        Assert.Contains("cgroup.events", linux, StringComparison.Ordinal);
        Assert.Contains("cgroup.kill", linux, StringComparison.Ordinal);
        Assert.Contains("proc_listpgrppids", mac, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(GetRepositoryRoot(), relativePath));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException(
                $"The fixture did not provide '{propertyName}'.");
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
