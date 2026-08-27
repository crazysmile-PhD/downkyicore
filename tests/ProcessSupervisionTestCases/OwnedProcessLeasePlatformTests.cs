using System.Diagnostics;
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
        Assert.Equal(outcome.Ownership.MembershipId, probe.MembershipId);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Ownership.BackendArchitecture));

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
    public async Task OwnershipEstablishmentFailureCannotAuthorizeTargetExecution()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-unowned-target-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--owned-tree-probe", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4));

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                    () => OwnedProcessLease.StartForTestingAsync(
                        launchSpec,
                        budget,
                        ProcessOwnershipMutation.FailOwnershipEstablishment,
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);
            Assert.False(File.Exists(readyPath));
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public void LinuxDelegatedHierarchyRootIsAValidMembershipAuthority()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(
            Path.GetFullPath("/sys/fs/cgroup"),
            LinuxCgroupContainmentLease.ResolveMembershipDirectory("/"));
    }

    [Fact]
    public async Task LinuxFailedMembershipAttachmentReapsAndRemovesTheStagedCgroup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var parentDirectory = LinuxCgroupContainmentLease.ResolveCurrentMembershipDirectory();
        var before = Directory.GetDirectories(parentDirectory, "downkyi-lease-*")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert.ThrowsAnyAsync<Exception>(
                () => RunProbeAsync(ProcessOwnershipMutation.FailAfterMembershipAttachment))
            .ConfigureAwait(true);

        var after = Directory.GetDirectories(parentDirectory, "downkyi-lease-*")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(before, after);
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
    public async Task TargetStartFailureFailsBeforeReturningAUsableLease()
    {
        var launchSpec = new LaunchSpec(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
            Array.Empty<string>(),
            Path.GetTempPath());

        await Assert.ThrowsAnyAsync<Exception>(
                () => RunAsync(launchSpec, ProcessOwnershipMutation.None))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task CallerCancellationStillCompletesOwnedCleanupBeforePropagating()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-owned-tree-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--owned-tree-probe", readyPath },
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
        try
        {
            var processIds = await WaitForOwnedTreeProbeAsync(
                    readyPath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(processIds.RootProcessId > 0);
            Assert.True(processIds.ChildProcessId > 0);
            Assert.NotEqual(processIds.RootProcessId, processIds.ChildProcessId);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(true);

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(cancellation.Token))
                .ConfigureAwait(true);
            Assert.Equal(OwnedProcessFailureKind.CallerCancelled, failure.Failure.Kind);
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task ParentExitAndInheritedStreamsRemainOwnedUntilTreeCleanupCompletes()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-parent-exit-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--exit-with-owned-descendant", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                ProcessOwnershipMutation.None,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            stopwatch.Stop();
            Assert.Equal(OwnedProcessFailureKind.OwnedTreeNotQuiescent, failure.Failure.Kind);
            Assert.False(failure.Failure.TreeQuiescent);
            Assert.NotNull(failure.Failure.TargetExitedAtUnixMilliseconds);
            Assert.Empty(failure.CleanupFailures);
            var processIds = await WaitForOwnedTreeProbeAsync(
                    readyPath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(processIds.RootProcessId > 0);
            Assert.True(processIds.ChildProcessId > 0);
            Assert.NotEqual(processIds.RootProcessId, processIds.ChildProcessId);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task InheritedOutputHandleCannotCreateAnUnboundedCompletion()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-held-stream-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--exit-with-owned-descendant", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                ProcessOwnershipMutation.None,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            stopwatch.Stop();
            Assert.Equal(
                OwnedProcessFailureKind.OwnedTreeNotQuiescent,
                failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            Assert.True(File.Exists(readyPath));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Theory]
    [InlineData((int)ProcessOwnershipMutation.FailAfterContainmentTermination,
        "Injected containment termination failure.")]
    [InlineData((int)ProcessOwnershipMutation.FailAfterRootReap,
        "Injected root reap failure.")]
    public async Task TerminateAndReapFailuresRemainVisibleAfterBoundedCleanup(
        int mutationValue,
        string expectedFailure)
    {
        var mutation = (ProcessOwnershipMutation)mutationValue;
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--block-forever" },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                mutation,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);

        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => lease.WaitAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(OwnedProcessFailureKind.OperationDeadlineExceeded, failure.Failure.Kind);
        Assert.Contains(
            failure.CleanupFailures,
            candidate => candidate.Message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MembershipAuthorityFailureCannotReportACompletedLease()
    {
        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => RunProbeAsync(ProcessOwnershipMutation.FailMembershipQuery))
            .ConfigureAwait(true);

        Assert.Equal(OwnedProcessFailureKind.ExecutionFailed, failure.Failure.Kind);
        Assert.Contains(
            new[] { failure.InnerException! }.Concat(failure.CleanupFailures),
            candidate => candidate.ToString().Contains(
                "authoritative membership-query failure",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MacOSReapingAnchorBeforeMembershipProofFailsClosed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => RunProbeAsync(ProcessOwnershipMutation.ReleaseAnchorBeforeMembership))
            .ConfigureAwait(true);

        Assert.Equal(OwnedProcessFailureKind.ExecutionFailed, failure.Failure.Kind);
        Assert.False(failure.Failure.TreeQuiescent);
        Assert.NotNull(failure.Failure.TargetExitedAtUnixMilliseconds);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.NotEmpty(failure.CleanupFailures);
    }

    [Fact]
    public async Task OwnerLifetimeClosureTriggersBoundedOwnedTreeCleanup()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-owner-death-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--owned-tree-probe", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                ProcessOwnershipMutation.None,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await WaitForOwnedTreeProbeAsync(
                    readyPath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            lease.CloseOwnerLifetimeForTesting();

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(TestContext.Current.CancellationToken))
                .ConfigureAwait(true);
            stopwatch.Stop();

            Assert.Equal(OwnedProcessFailureKind.ExecutionFailed, failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task OwnerDeathAfterTargetExitStillTerminatesRetainedDescendants()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-owner-death-after-exit-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--exit-with-owned-descendant", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                launchSpec,
                budget,
                ProcessOwnershipMutation.None,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        try
        {
            _ = await WaitForOwnedTreeProbeAsync(
                    readyPath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            _ = await lease.WaitForTargetExitForTestingAsync()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            lease.CloseOwnerLifetimeForTesting();

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(TestContext.Current.CancellationToken))
                .ConfigureAwait(true);
            Assert.Empty(failure.CleanupFailures);
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    [Fact]
    public async Task StalledLargeLaunchPayloadConsumesTheCallerBudget()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var environment = new Dictionary<string, string?>
        {
            ["DOWNKYI_LARGE_LAUNCH_PAYLOAD"] = new string('x', 900_000)
        };
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--ownership-probe" },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."),
            environment);
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<Exception>(
                () => OwnedProcessLease.StartForTestingAsync(
                    launchSpec,
                    budget,
                    ProcessOwnershipMutation.StallLaunchPayloadRead,
                    TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        stopwatch.Stop();

        Assert.Contains("launch specification", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TargetExitTimestampPrecedesPostExitSupervisorLatency()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outcome = await RunProbeAsync(ProcessOwnershipMutation.DelayAfterTargetExitReport)
            .ConfigureAwait(true);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.InRange(outcome.TargetExitedAtUnixMilliseconds, before, after);
        Assert.True(after - outcome.TargetExitedAtUnixMilliseconds >= 200);
    }

    [Fact]
    public async Task FailedFixturePublicationRemovesItsTemporaryFile()
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var readyPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-publication-failure-{Guid.NewGuid():N}.json");
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { assemblyPath, "--exit-with-owned-descendant", readyPath },
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The probe directory is unavailable."));
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
        try
        {
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => RunAsync(
                        launchSpec,
                        ProcessOwnershipMutation.FailFixturePublication,
                        budget))
                .ConfigureAwait(true);

            Assert.NotNull(failure);
            Assert.NotNull(failure.Failure.TargetExitedAtUnixMilliseconds);
            Assert.False(File.Exists(readyPath));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(readyPath)!,
                $"{Path.GetFileName(readyPath)}.*.tmp"));
        }
        finally
        {
            File.Delete(readyPath);
            foreach (var temporaryPath in Directory.GetFiles(
                         Path.GetDirectoryName(readyPath)!,
                         $"{Path.GetFileName(readyPath)}.*.tmp"))
            {
                File.Delete(temporaryPath);
            }
        }
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
        ProcessOwnershipMutation mutation,
        TransitionBudget? budget = null)
    {
        budget ??= TransitionBudget.Start(
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

    private static (
        string ContainmentKind,
        string ContainmentId,
        string MembershipId,
        bool OwnershipEstablished) ReadProbe(
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
            root.GetProperty("MembershipId").GetString()
                ?? throw new InvalidOperationException("The membership identity is missing."),
            root.GetProperty("OwnershipEstablished").GetBoolean());
    }

    private static async Task<(int RootProcessId, int ChildProcessId)> WaitForOwnedTreeProbeAsync(
        string readyPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(readyPath)
            ?? throw new InvalidOperationException("The owned-tree probe directory is unavailable.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(readyPath));
        FileSystemEventHandler created = (_, _) => completion.TrySetResult();
        RenamedEventHandler renamed = (_, _) => completion.TrySetResult();
        watcher.Created += created;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(readyPath))
        {
            completion.TrySetResult();
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(readyPath, cancellationToken).ConfigureAwait(false));
        return (
            document.RootElement.GetProperty("RootProcessId").GetInt32(),
            document.RootElement.GetProperty("ChildProcessId").GetInt32());
    }
}

public sealed class PosixProcessGroupTerminationTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-1, 3, false)]
    [InlineData(-1, 1, true)]
    public void PosixTerminationDefersDarwinNoSignalableGroupToMembershipAuthority(
        int result,
        int error,
        bool darwinMembershipAuthority)
    {
        PosixProcessGroupTermination.ValidateTerminationRequestResult(
            result,
            error,
            darwinMembershipAuthority);
    }

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(-1, 22, true)]
    public void PosixTerminationRejectsResultsWithoutAuthoritativeConvergence(
        int result,
        int error,
        bool darwinMembershipAuthority)
    {
        var failure = Assert.Throws<System.ComponentModel.Win32Exception>(
            () => PosixProcessGroupTermination.ValidateTerminationRequestResult(
                result,
                error,
                darwinMembershipAuthority));

        Assert.Equal(error, failure.NativeErrorCode);
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
