namespace DownKyi.Architecture.Tests;

public sealed class RestartHandoffArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductRestartUsesOnlyTheRestartSpecificLifecycleBoundary()
    {
        var launcher = ReadSource(
            "src",
            "DownKyi.Desktop",
            "Platform",
            "ProcessRestartLauncher.cs");

        Assert.Contains("RestartHandoffLease.PrepareAsync", launcher, StringComparison.Ordinal);
        Assert.Contains("RestartHandoffHelper.ExecuteAsync", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcessById", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTime", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("AnonymousPipe", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnedProcessLease", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("restart-after-pid", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffOwnsOnePreparedDeadlineAndNoFreshHelperBudget()
    {
        var lease = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "RestartHandoffLease.cs");
        var contracts = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "RestartHandoffContracts.cs");

        Assert.Contains("budget.CreateRestartHandoffDeadline()", lease, StringComparison.Ordinal);
        Assert.DoesNotContain("TransitionBudget.Start", lease, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(30)", lease, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch.StartNew", lease, StringComparison.Ordinal);
        Assert.Contains("OperationExpiresAt", contracts, StringComparison.Ordinal);
        Assert.Contains("CleanupExpiresAt", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperCleanupPreservesThePrimaryOutcomeAndAttemptsEveryStage()
    {
        var lease = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "RestartHandoffLease.cs");
        var contracts = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "RestartHandoffContracts.cs");

        Assert.Contains("CaptureCleanupFailureAsync", lease, StringComparison.Ordinal);
        Assert.Contains(
            "RestartHandoffCleanupStage.StatusEndpoint",
            lease,
            StringComparison.Ordinal);
        Assert.Contains(
            "RestartHandoffCleanupStage.AuthorizationEndpoint",
            lease,
            StringComparison.Ordinal);
        Assert.Contains(
            "RestartHandoffCleanupStage.ParentLifetime",
            lease,
            StringComparison.Ordinal);
        Assert.Contains("CleanupFailures = readOnlyCleanupFailures", lease, StringComparison.Ordinal);
        Assert.Contains("CleanupFailures.Count == 0", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactParentBackendsHaveNoPidOrSleepFallback()
    {
        var source = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "ParentLifetimeLeases.cs");

        Assert.Contains("OpenProcess(Synchronize", source, StringComparison.Ordinal);
        Assert.Contains("PidfdOpenSystemCall", source, StringComparison.Ordinal);
        Assert.Contains("EventFilterProcess", source, StringComparison.Ordinal);
        Assert.Contains("NoteExit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcessById", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/proc", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryLeaseRetainsOwnerDeathSemanticsWithoutRestartExceptions()
    {
        var source = ReadSource(
            "tools",
            "DownKyi.ProcessSupervision",
            "OwnedProcessLease.cs");

        Assert.DoesNotContain("IgnoreOwnerDeathAfterCommit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartHandoff", source, StringComparison.Ordinal);
        Assert.DoesNotContain("detach", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
