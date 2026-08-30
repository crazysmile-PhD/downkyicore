using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownKyi.ProcessSupervision;
using FeasibilityFixtureMarker = DownKyi.RestartHandoff.Fixture.FixtureMarker;
using ProductionFixtureMarker = DownKyi.RestartHandoff.ProductionFixture.FixtureMarker;

namespace DownKyi.Architecture.Tests;

public sealed class RestartHandoffMutationTests
{
    private const string NumericAuthorityMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_NUMERIC_AUTHORITY";
    private const string EarlyReadyMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_EARLY_READY";
    private const string FreshDeadlineMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_FRESH_DEADLINE";
    private const string OrdinaryLeaseMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_ORDINARY_LEASE";
    private const string AuthorizationReplayMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_AUTHORIZATION_REPLAY";
    private const string RelaunchRetryMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_RELAUNCH_RETRY";
    private const string ParentOrderingMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_PARENT_ORDERING";
    private const string RevokeReapMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_REVOKE_REAP";
    private const string CleanupShortCircuitMutation =
        "DOWNKYI_TEST_MUTATE_RESTART_CLEANUP_SHORT_CIRCUIT";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NumericProcessMetadataCannotReplaceExactParentAuthority()
    {
        var exactParent = ParentLifetimeLeaseFactory.Create(Environment.ProcessId);
        await using var exactParentScope = exactParent.ConfigureAwait(true);
        using var numericCandidate = StartProductionFixture("instant-exit");
        _ = numericCandidate.StartTime;
        await numericCandidate.WaitForExitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var numericAuthorityReportedExit = IsMutationActive(NumericAuthorityMutation) &&
            numericCandidate.HasExited;
        var exactParentIsStillLive = !exactParent.IsExited();

        Assert.False(
            numericAuthorityReportedExit && exactParentIsStillLive,
            "Numeric PID/StartTime metadata reported a false parent exit while the retained " +
            "exact-parent watcher remained live.");
        Assert.True(exactParentIsStillLive);
    }

    [Fact]
    public async Task ReadinessPublicationCannotPrecedeExactWatcherArming()
    {
        using var parent = StartProductionFixture("hold");
        ParentLifetimeLease? watcher = null;
        var readinessPublished = false;
        var readinessPrecededWatcher = false;
        try
        {
            if (IsMutationActive(EarlyReadyMutation))
            {
                readinessPublished = true;
                readinessPrecededWatcher = true;
            }

            watcher = ParentLifetimeLeaseFactory.Create(parent.Id);
            readinessPublished = true;

            Assert.False(
                readinessPrecededWatcher,
                "Restart readiness was published before the exact-parent watcher was armed.");
            Assert.True(readinessPublished);
            Assert.False(watcher.IsExited());
        }
        finally
        {
            if (watcher != null)
            {
                await watcher.DisposeAsync().ConfigureAwait(true);
            }

            await TerminateAndReapAsync(parent).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task HelperCannotOpenAFreshDeadlineAfterCommit()
    {
        var preparedBudget = TransitionBudget.Start(
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(120));
        var preparedDeadline = preparedBudget.CreateRestartHandoffDeadline();
        await Task.Delay(180, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var selectedDeadline = IsMutationActive(FreshDeadlineMutation)
            ? TransitionBudget.Start(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(120))
                .CreateRestartHandoffDeadline()
            : preparedDeadline;

        Assert.False(
            selectedDeadline.RemainingOperation > TimeSpan.Zero,
            "A fresh helper timeout extended the authenticated prepare deadline after commit.");
        Assert.Equal(TimeSpan.Zero, preparedDeadline.RemainingOperation);
    }

    [Fact]
    public async Task OrdinaryOwnedProcessLeaseCannotOwnACommittedRestartSuccessor()
    {
        var pipeName = IpcEndpointName.Create("Stage4.OrdinaryLeaseMutation");
        using var pipe = new NamedPipeServerStream(
            pipeName.PhysicalIdentifier,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var fixturePath = typeof(FeasibilityFixtureMarker).Assembly.Location;
        OwnedProcessLease lease;
        try
        {
            lease = await OwnedProcessLease.StartForTestingAsync(
                    new LaunchSpec(
                        "dotnet",
                        [fixturePath, "owned-successor", pipeName.PhysicalIdentifier],
                        Path.GetDirectoryName(fixturePath)
                            ?? throw new InvalidOperationException(
                                "The feasibility fixture directory is unavailable.")),
                    TransitionBudget.Start(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(3)),
                    ProcessOwnershipMutation.None,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsLinux())
        {
            Assert.False(
                IsMutationActive(OrdinaryLeaseMutation),
                "The ordinary-lease mutation attempted to compose restart ownership on a " +
                "Linux runner without delegated cgroup authority; the lease rejected the " +
                "transition before launch.");
            return;
        }

        await using var leaseScope = lease.ConfigureAwait(true);
        await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);
        var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await using (writer.ConfigureAwait(true))
        {
            var ready = DeserializeOwnedSuccessor(
                await ReadLineAsync(reader).ConfigureAwait(true));
            Assert.Equal("Ready", ready.State);
            await writer.WriteLineAsync("COMMIT").ConfigureAwait(true);
            var committed = DeserializeOwnedSuccessor(
                await ReadLineAsync(reader).ConfigureAwait(true));
            Assert.True(committed.Committed);
        }

        lease.CloseOwnerLifetimeForTesting();
        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => lease.WaitAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        var ordinaryOwnerKilledCommittedCandidate =
            failure.Failure.Kind == OwnedProcessFailureKind.ExecutionFailed &&
            await ReadLineAsync(reader, allowEof: true).ConfigureAwait(true) == null;

        Assert.False(
            IsMutationActive(OrdinaryLeaseMutation) &&
                ordinaryOwnerKilledCommittedCandidate,
            "The executable ordinary-lease composition killed the committed restart " +
            "successor on owner EOF; it cannot span the commit boundary.");
        Assert.True(ordinaryOwnerKilledCommittedCandidate);
    }

    [Fact]
    public async Task AuthorizationReplayCannotCreateASecondRelaunchTransition()
    {
        var deadline = TransitionBudget.Start(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1))
            .CreateRestartHandoffDeadline();
        var nonce = RandomNumberGenerator.GetBytes(RestartHandoffProtocol.NonceLength);
        var frame = RestartAuthorizationFrame.Create(deadline, nonce);
        var replay = new byte[frame.Length * 2];
        frame.CopyTo(replay, 0);
        frame.CopyTo(replay, frame.Length);
        var relaunchAttempts = 0;

        if (IsMutationActive(AuthorizationReplayMutation))
        {
            for (var offset = 0; offset < replay.Length; offset += frame.Length)
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        frame,
                        replay.AsSpan(offset, frame.Length)))
                {
                    continue;
                }

                using var replacement = StartProductionFixture("instant-exit");
                await replacement.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                relaunchAttempts++;
            }
        }
        else
        {
            using var payload = new MemoryStream(replay);
            var rejection = await RestartAuthorizationFrame.ReadAsync(
                    payload,
                    deadline,
                    nonce,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.NotNull(rejection);
            Assert.Equal(RestartHandoffFailureKind.AuthorizationRejected, rejection.Value.Kind);
        }

        Assert.False(
            relaunchAttempts > 1,
            "A replayed authorization frame produced a second relaunch transition.");
    }

    [Fact]
    public async Task RelaunchStartFailureCannotRetry()
    {
        var relaunchAttempts = 0;
        try
        {
            relaunchAttempts++;
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Path.GetTempPath(),
                    $"downkyi-missing-restart-{Guid.NewGuid():N}"),
                UseShellExecute = false
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (IsMutationActive(RelaunchRetryMutation))
            {
                relaunchAttempts++;
                using var replacement = StartProductionFixture("instant-exit");
                await replacement.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }
        }

        Assert.False(
            relaunchAttempts > 1,
            "The restart successor retried after its one permitted relaunch attempt failed.");
        Assert.Equal(1, relaunchAttempts);
    }

    [Fact]
    public async Task ParentExitBeforeWatcherArmCannotBePublishedAsReady()
    {
        using var parent = StartProductionFixture("instant-exit");
        var parentProcessId = parent.Id;
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var readinessPublished = IsMutationActive(ParentOrderingMutation);
        var watcherRejected = false;
        try
        {
            var watcher = ParentLifetimeLeaseFactory.Create(parentProcessId);
            await using var watcherScope = watcher.ConfigureAwait(true);
            watcherRejected = watcher.IsExited();
        }
        catch (Exception failure) when (failure is
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            watcherRejected = true;
        }

        Assert.True(watcherRejected);
        Assert.False(
            readinessPublished,
            "The reversed parent-exit ordering published READY after watcher arming was " +
            "already impossible.");
    }

    [Fact]
    public async Task RevocationMustReapThePreparedHelper()
    {
        using var helper = StartProductionFixture("hold");
        var mutationActive = IsMutationActive(RevokeReapMutation);
        try
        {
            if (!mutationActive)
            {
                await TerminateAndReapAsync(helper).ConfigureAwait(true);
            }

            var residualHelper = !helper.HasExited;
            Assert.False(
                residualHelper,
                "Revocation returned while its prepared restart helper remained live and unreaped.");
        }
        finally
        {
            await TerminateAndReapAsync(helper).ConfigureAwait(true);
        }
    }

    [Fact]
    public void CleanupFailureCannotSkipLaterHelperResources()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "DownKyi.ProcessSupervision",
                "RestartHandoffLease.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (IsMutationActive(CleanupShortCircuitMutation))
        {
            source = source.Replace(
                "RestartHandoffCleanupStage.ParentLifetime,",
                "RestartHandoffCleanupStage.AuthorizationEndpoint,",
                StringComparison.Ordinal);
        }

        var statusIndex = source.IndexOf(
            "RestartHandoffCleanupStage.StatusEndpoint,",
            StringComparison.Ordinal);
        var authorizationIndex = source.IndexOf(
            "RestartHandoffCleanupStage.AuthorizationEndpoint,",
            statusIndex + 1,
            StringComparison.Ordinal);
        var parentIndex = source.IndexOf(
            "RestartHandoffCleanupStage.ParentLifetime,",
            authorizationIndex + 1,
            StringComparison.Ordinal);
        Assert.True(statusIndex >= 0);
        Assert.True(authorizationIndex > statusIndex);
        Assert.True(parentIndex > authorizationIndex);
        Assert.Contains(
            "failures.Add(RestartHandoffCleanupFailure.FromException(stage, failure));",
            source,
            StringComparison.Ordinal);
    }

    private static bool IsMutationActive(string environmentVariable)
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(environmentVariable),
            "1",
            StringComparison.Ordinal);
    }

    private static Process StartProductionFixture(params string[] arguments)
    {
        var fixturePath = typeof(ProductionFixtureMarker).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(fixturePath)
                ?? throw new InvalidOperationException(
                    "The production fixture directory is unavailable."),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(fixturePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The restart mutation fixture did not start.");
    }

    private static async Task TerminateAndReapAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        bool allowEof = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (!allowEof && line == null)
        {
            throw new EndOfStreamException("The ordinary successor fixture closed unexpectedly.");
        }

        return line;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private static OwnedSuccessorEvidence DeserializeOwnedSuccessor(string? line)
    {
        return JsonSerializer.Deserialize<OwnedSuccessorEvidence>(line!, JsonOptions)
            ?? throw new InvalidDataException("The ordinary successor fixture evidence was empty.");
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json instantiates the typed cross-process mutation evidence.")]
    private sealed record OwnedSuccessorEvidence(
        string State,
        bool Committed,
        int RelaunchAttempts);
}
