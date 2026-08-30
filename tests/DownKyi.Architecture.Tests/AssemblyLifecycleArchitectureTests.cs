using System.Text.RegularExpressions;

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
