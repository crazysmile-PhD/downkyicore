using System.Text.Json;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class OwnedProcessLeasePlatformTests
{
    private static readonly char[] LineSeparators = ['\r', '\n'];

    [Fact]
    public async Task TargetExecutesOnlyAfterPlatformOwnershipIsEstablished()
    {
        var outcome = await RunProbeAsync(ProcessOwnershipMutation.None).ConfigureAwait(true);
        var probe = ReadProbe(outcome.StandardOutput);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
        Assert.True(outcome.Ownership.OwnershipEstablished);
        Assert.True(probe.OwnershipEstablished);
        Assert.Equal(outcome.Ownership.ContainmentKind.ToString(), probe.ContainmentKind);
        Assert.Equal(outcome.Ownership.ContainmentId, probe.ContainmentId);

        if (OperatingSystem.IsWindows() &&
            string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(outcome.Ownership.OwnerWasAlreadyContained);
        }
    }

    [Fact]
    public async Task LaunchTimeOwnershipMutationFailsTheBehavioralProof()
    {
        var outcome = await RunProbeAsync(
                ProcessOwnershipMutation.ResumeTargetBeforeOwnership)
            .ConfigureAwait(true);
        var probe = ReadProbe(outcome.StandardOutput);

        Assert.Equal(42, outcome.ExitCode);
        Assert.False(outcome.Ownership.OwnershipEstablished);
        Assert.False(probe.OwnershipEstablished);
    }

    [Fact]
    public async Task LaunchSpecSnapshotsCallerOwnedArgumentsAndEnvironment()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var arguments = new List<string>
        {
            assemblyPath,
            "--launch-spec-probe",
            "original-argument"
        };
        var environment = new Dictionary<string, string?>
        {
            ["DOWNKYI_LAUNCH_SPEC_PROBE"] = "original-environment"
        };
        var launchSpec = new LaunchSpec(
            "dotnet",
            arguments,
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."),
            environment,
            closeStandardInput: true);

        arguments[2] = "mutated-argument";
        environment["DOWNKYI_LAUNCH_SPEC_PROBE"] = "mutated-environment";

        var outcome = await RunAsync(launchSpec, ProcessOwnershipMutation.None)
            .ConfigureAwait(true);
        using var document = JsonDocument.Parse(outcome.StandardOutput);
        Assert.Equal(
            "original-argument",
            document.RootElement.GetProperty("Argument").GetString());
        Assert.Equal(
            "original-environment",
            document.RootElement.GetProperty("EnvironmentValue").GetString());
    }

    [Fact]
    public async Task TargetStartFailureRemainsFailedAndTreeQuiescent()
    {
        var launchSpec = new LaunchSpec(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
            Array.Empty<string>(),
            Path.GetTempPath());

        var outcome = await RunAsync(launchSpec, ProcessOwnershipMutation.None)
            .ConfigureAwait(true);

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
        Assert.True(outcome.Ownership.OwnershipEstablished);
    }

    [Fact]
    public async Task CallerCancellationStillCompletesOwnedCleanupBeforePropagating()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--block-forever" },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                ProcessOwnershipMutation.None,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => lease.WaitAsync(cancellation.Token))
            .ConfigureAwait(true);
    }

    private static async Task<OwnedProcessOutcome> RunProbeAsync(
        ProcessOwnershipMutation mutation)
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--ownership-probe" },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."),
            closeStandardInput: true);
        return await RunAsync(launchSpec, mutation).ConfigureAwait(true);
    }

    private static async Task<OwnedProcessOutcome> RunAsync(
        LaunchSpec launchSpec,
        ProcessOwnershipMutation mutation)
    {
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                mutation,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        return await lease.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static (string ContainmentKind, string ContainmentId, bool OwnershipEstablished) ReadProbe(
        string standardOutput)
    {
        var line = standardOutput
            .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Single();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return (
            root.GetProperty("ContainmentKind").GetString()
                ?? throw new InvalidOperationException("The containment kind is missing."),
            root.GetProperty("ContainmentId").GetString()
                ?? throw new InvalidOperationException("The containment identity is missing."),
            root.GetProperty("OwnershipEstablished").GetBoolean());
    }
}

public sealed class TransitionBudgetTests
{
    [Fact]
    public void OperationAndCleanupConsumeOneMonotonicTimeline()
    {
        var timeProvider = new ManualTimeProvider();
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2),
            timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(1), budget.RemainingOperation);
        Assert.Equal(TimeSpan.FromSeconds(3), budget.RemainingCleanup);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, budget.RemainingOperation);
        Assert.Equal(TimeSpan.FromSeconds(1), budget.RemainingCleanup);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, budget.RemainingCleanup);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        }

        public void Advance(TimeSpan duration)
        {
            _timestamp = checked(_timestamp + duration.Ticks);
        }
    }
}
