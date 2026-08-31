using DownKyi.ProcessSupervision;

namespace DownKyi.Platform.Tests;

public sealed class OwnedProcessLeaseBehaviorTests
{
    [Fact]
    public async Task RealProcessCompletesWithEveryRequiredInvariantProven()
    {
        var lease = await StartAsync(CreateOutputCommand());
        try
        {
            var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(outcome.FormalGatePassed);
            Assert.Equal(7, outcome.ExitCode);
            Assert.Contains("stdout-proof", outcome.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("stderr-proof", outcome.StandardError, StringComparison.Ordinal);
            Assert.All(
                outcome.Invariants,
                invariant => Assert.Equal(
                    OwnedProcessInvariantState.Proven,
                    invariant.State));
            Assert.Empty(outcome.Failures);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DotnetTargetCompletesWithInfrastructureBaseline(int iteration)
    {
        var lease = await StartAsync(CreateDotnetVersionCommand());
        try
        {
            var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(outcome.FormalGatePassed);
            Assert.Equal(0, outcome.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(outcome.StandardOutput));
            Assert.Empty(outcome.Failures);
            Assert.InRange(iteration, 1, 3);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task PersistentDescendantFailsBudgetAndCompletesOwnerCleanupProof()
    {
        var lease = await StartAsync(
            CreatePersistentDescendantCommand(),
            TransitionBudget.Start(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        try
        {
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => lease.WaitAsync(TestContext.Current.CancellationToken));
            var outcome = failure.Outcome;

            Assert.False(outcome.FormalGatePassed);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.TargetTerminal,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.RequiredContainment,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.OperationCompletion,
                OwnedProcessInvariantState.Violated);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.OperationBudget,
                OwnedProcessInvariantState.Violated);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.TreeQuiescence,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.BoundedCleanup,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.StreamDrain,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.OwnershipLifetime,
                OwnedProcessInvariantState.Proven);
            Assert.DoesNotContain(
                outcome.Invariants,
                invariant => invariant.State == OwnedProcessInvariantState.Unknown);
            Assert.Contains(
                outcome.Failures,
                item => item.Kind == OwnedProcessFailureKind.OperationDeadlineExceeded &&
                    item.Channel == OwnedProcessFailureChannel.Operation);
            AssertFact(
                outcome,
                OwnedProcessFactKind.OperationDeadlineExceeded);
            AssertFact(
                outcome,
                OwnedProcessFactKind.TerminationCompleted,
                OwnedProcessFailurePhase.Termination);
            AssertFact(
                outcome,
                OwnedProcessFactKind.TreeQuiescent,
                OwnedProcessFailurePhase.Quiescence);
            AssertFact(
                outcome,
                OwnedProcessFactKind.CleanupCompleted,
                OwnedProcessFailurePhase.ResourceRelease);
            AssertFact(
                outcome,
                OwnedProcessFactKind.StreamsDrained,
                OwnedProcessFailurePhase.StreamDrain);
            AssertFact(
                outcome,
                OwnedProcessFactKind.OwnershipClosed,
                OwnedProcessFailurePhase.ResourceRelease);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task CallerCancellationFailsGateButStillProvesBoundedCleanup()
    {
        var lease = await StartAsync(CreateBlockingCommand());
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => lease.WaitAsync(cancellation.Token));
            var outcome = failure.Outcome;

            Assert.False(outcome.FormalGatePassed);
            Assert.Contains(
                outcome.Failures,
                item => item.Kind == OwnedProcessFailureKind.CallerCancelled);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.OperationCompletion,
                OwnedProcessInvariantState.Violated);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.TreeQuiescence,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.BoundedCleanup,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.StreamDrain,
                OwnedProcessInvariantState.Proven);
            AssertInvariant(
                outcome,
                OwnedProcessInvariantKind.OwnershipLifetime,
                OwnedProcessInvariantState.Proven);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task LifetimeCloseIsTheOnlyOwnerWhenWaitWasNeverCalled()
    {
        var lease = await StartAsync(CreateBlockingCommand());

        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
            async () => await lease.DisposeAsync().ConfigureAwait(false));

        Assert.Contains(
            failure.Outcome.Failures,
            item => item.Kind == OwnedProcessFailureKind.LifetimeClosed);
        AssertInvariant(
            failure.Outcome,
            OwnedProcessInvariantKind.TreeQuiescence,
            OwnedProcessInvariantState.Proven);
        AssertInvariant(
            failure.Outcome,
            OwnedProcessInvariantKind.OwnershipLifetime,
            OwnedProcessInvariantState.Proven);
    }

    private static Task<OwnedProcessLease> StartAsync(LaunchSpec launchSpec)
    {
        return StartAsync(
            launchSpec,
            TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
    }

    private static Task<OwnedProcessLease> StartAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget)
    {
        return OwnedProcessLease.StartAsync(
            launchSpec,
            budget,
            ProcessContainmentRequirement.AllowWeakerFallback);
    }

    private static LaunchSpec CreateOutputCommand()
    {
        return OperatingSystem.IsWindows()
            ? CreateCommand("echo stdout-proof & echo stderr-proof 1>&2 & exit /b 7")
            : CreateCommand("printf 'stdout-proof\\n'; printf 'stderr-proof\\n' >&2; exit 7");
    }

    private static LaunchSpec CreateBlockingCommand()
    {
        return OperatingSystem.IsWindows()
            ? CreateCommand("ping 127.0.0.1 -n 30 > nul")
            : CreateCommand("sleep 30");
    }

    private static LaunchSpec CreateDotnetVersionCommand()
    {
        return new LaunchSpec(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["--version"],
            Directory.GetCurrentDirectory());
    }

    private static LaunchSpec CreatePersistentDescendantCommand()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException(
                "The platform test configuration directory is unavailable.");
        var probeAssembly = Path.Combine(
            repositoryRoot,
            "tools",
            "DownKyi.AssemblyLifecycleProbe",
            "bin",
            configuration,
            "net10.0",
            "DownKyi.AssemblyLifecycleProbe.dll");
        if (!File.Exists(probeAssembly))
        {
            throw new InvalidOperationException(
                "The persistent-descendant probe build output is unavailable.");
        }

        return new LaunchSpec(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [probeAssembly, "--spawn-residual-child-ms", "30000"],
            repositoryRoot);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root is unavailable.");
    }

    private static LaunchSpec CreateCommand(string command)
    {
        var fileName = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", command }
            : new[] { "-c", command };
        return new LaunchSpec(fileName, arguments, Directory.GetCurrentDirectory());
    }

    private static void AssertInvariant(
        OwnedProcessOutcome outcome,
        OwnedProcessInvariantKind kind,
        OwnedProcessInvariantState state)
    {
        Assert.Equal(state, outcome.Invariants.Single(item => item.Kind == kind).State);
    }

    private static void AssertFact(
        OwnedProcessOutcome outcome,
        OwnedProcessFactKind kind)
    {
        Assert.Contains(outcome.Facts, fact => fact.Kind == kind);
    }

    private static void AssertFact(
        OwnedProcessOutcome outcome,
        OwnedProcessFactKind kind,
        OwnedProcessFailurePhase phase)
    {
        Assert.Contains(
            outcome.Facts,
            fact => fact.Kind == kind && fact.Phase == phase);
    }
}
