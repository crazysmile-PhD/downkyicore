using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DownKyi.RestartHandoff.ProductionFixture;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class RestartHandoffProductionTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ProductionCommitRelaunchesExactlyOnceAndClosesHelper()
    {
        await using var scenario = StartScenario("normal", 8_000);
        var prepared = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        Assert.Equal(RestartHandoffState.Authorized, prepared.State);
        Assert.Equal(ExpectedAuthority(), prepared.Authority);

        await scenario.SendAsync("COMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Single(evidence, item => item.Type == "Committed");
        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(RestartHandoffState.Completed, terminal.State);
        Assert.Null(terminal.FailureKind);
        Assert.Equal(1, terminal.RelaunchAttempts);
        Assert.Single(evidence, item => item.Type == "ReplacementStarted");
        Assert.False(scenario.ForcedCleanup);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task ProductionRevokeTerminatesAndReapsPreparedHelper()
    {
        await using var scenario = StartScenario("revoke", 8_000);
        var prepared = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);

        await scenario.SendAsync("REVOKE").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var revoked = Assert.Single(evidence, item => item.Type == "Revoked");
        Assert.Equal(RestartHandoffState.Revoked, revoked.State);
        Assert.Equal(prepared.ProcessId, revoked.ProcessId);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.False(scenario.ForcedCleanup);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task ProductionStaleIdentityFailsBeforeAuthorization()
    {
        await using var scenario = StartScenario("stale-identity", 5_000);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var failure = Assert.Single(evidence, item => item.Type == "RestartFailure");
        Assert.True(
            failure.FailureKind is RestartHandoffFailureKind.WatcherFailed or
                RestartHandoffFailureKind.ParentExitedBeforeReady);
        Assert.DoesNotContain(evidence, item => item.Type == "Prepared");
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
    }

    [Fact]
    public async Task ParentExitAfterReadyBeforeCommitCannotRelaunch()
    {
        await using var scenario = StartScenario("parent-exit-before-commit", 5_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(RestartHandoffFailureKind.CommitChannelClosed, terminal.FailureKind);
        Assert.Equal(0, terminal.RelaunchAttempts);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task RelaunchStartFailureIsTypedOneShotAndTerminal()
    {
        await using var scenario = StartScenario("relaunch-failure", 8_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        await scenario.SendAsync("COMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(RestartHandoffFailureKind.RelaunchFailed, terminal.FailureKind);
        Assert.Equal(1, terminal.RelaunchAttempts);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task RelaunchFailureRemainsPrimaryWhileEveryCleanupStageIsAttempted()
    {
        await using var scenario = StartScenario("relaunch-failure-cleanup", 8_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        await scenario.SendAsync("COMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(RestartHandoffState.Failed, terminal.State);
        Assert.Equal(RestartHandoffFailureKind.RelaunchFailed, terminal.FailureKind);
        Assert.Equal(1, terminal.RelaunchAttempts);
        Assert.False(terminal.Succeeded);
        Assert.Equal(
            [
                RestartHandoffCleanupStage.StatusEndpoint,
                RestartHandoffCleanupStage.AuthorizationEndpoint,
                RestartHandoffCleanupStage.ParentLifetime
            ],
            terminal.CleanupFailures?.Select(failure => failure.Stage));
        Assert.All(
            terminal.CleanupFailures!,
            failure => Assert.Equal(
                typeof(InvalidOperationException).FullName,
                failure.CauseType));
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task CleanupOnlyFailureIsReportedWithoutInventingAPrimaryTransition()
    {
        await using var scenario = StartScenario("cleanup-only", 8_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        await scenario.SendAsync("COMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(RestartHandoffState.Completed, terminal.State);
        Assert.Null(terminal.FailureKind);
        Assert.False(terminal.Succeeded);
        var cleanupFailure = Assert.Single(terminal.CleanupFailures!);
        Assert.Equal(
            RestartHandoffCleanupStage.AuthorizationEndpoint,
            cleanupFailure.Stage);
        Assert.Single(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task DuplicateCommitIsRejectedWithoutSecondRelaunch()
    {
        await using var scenario = StartScenario("duplicate", 8_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        await scenario.SendAsync("DUPLICATE").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Single(evidence, item => item.Type == "DuplicateCommitRejected");
        Assert.Single(evidence, item => item.Type == "ReplacementStarted");
        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(1, terminal.RelaunchAttempts);
    }

    [Fact]
    public async Task PrecommitConsumptionReducesCommittedParentWaitDeadline()
    {
        await using var scenario = StartScenario("deadline", 1_800);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        var elapsed = Stopwatch.StartNew();
        await scenario.SendAsync("CONSUME:700").ConfigureAwait(true);
        await scenario.SendAsync("COMMIT_HOLD").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("Committed").ConfigureAwait(true);
        var terminal = await scenario.ReadExpectedAsync("HelperTerminal").ConfigureAwait(true);

        Assert.Equal(RestartHandoffFailureKind.DeadlineExceeded, terminal.FailureKind);
        Assert.Equal(0, terminal.RelaunchAttempts);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(2_600));
        Assert.False(scenario.ParentHasExited);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var remaining = await scenario.CompleteAsync().ConfigureAwait(true);
        Assert.DoesNotContain(remaining, item => item.Type == "ReplacementStarted");
    }

    [Fact]
    public async Task CancellationInterruptsAnEnteredNativeParentWaitAndIsTyped()
    {
        await using var scenario = StartScenario("parent-wait-cancellation", 8_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        var elapsed = Stopwatch.StartNew();
        await scenario.SendAsync("COMMIT_HOLD").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("Committed").ConfigureAwait(true);
        var terminal = await scenario.ReadExpectedAsync("HelperTerminal").ConfigureAwait(true);

        Assert.Equal(RestartHandoffState.Failed, terminal.State);
        Assert.Equal(RestartHandoffFailureKind.CancellationRequested, terminal.FailureKind);
        Assert.Equal(0, terminal.RelaunchAttempts);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4),
            $"Cancellation returned after {elapsed.Elapsed} instead of waking the native wait.");
        Assert.False(scenario.ParentHasExited);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var remaining = await scenario.CompleteAsync().ConfigureAwait(true);
        Assert.DoesNotContain(remaining, item => item.Type == "ReplacementStarted");
    }

    [Fact]
    public async Task EstablishedExactParentExitWinsACancellationRace()
    {
        using var parent = Process.Start(CreateFixtureStartInfo("hold"))
            ?? throw new InvalidOperationException("The exact-parent fixture did not start.");
        ParentLifetimeLease? watcher = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            watcher = ParentLifetimeLeaseFactory.Create(parent.Id);
            var deadline = TransitionBudget.Start(
                    TimeSpan.FromSeconds(6),
                    TimeSpan.FromSeconds(1))
                .CreateRestartHandoffDeadline();
            var waitStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waitTask = Task.Run(async () =>
                await watcher.WaitForExitForTestingAsync(
                        deadline,
                        () => waitStarted.TrySetResult(true),
                        cancellation.Token)
                    .ConfigureAwait(false));

            await waitStarted.Task.WaitAsync(
                    deadline.RemainingOperation,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            parent.Kill(entireProcessTree: true);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);

            var outcome = await waitTask.WaitAsync(
                    deadline.RemainingOperation,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(outcome.ExactParentExited);
            Assert.Equal(ExpectedAuthority(), watcher.IdentityAuthority);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
                await parent.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            if (watcher != null)
            {
                await watcher.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    [Fact]
    public async Task HelperCrashBeforeCommitFailsClosedWithoutResidualHelper()
    {
        await using var scenario = StartScenario("helper-crash-before-commit", 5_000);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var failure = Assert.Single(evidence, item => item.Type == "RestartFailure");
        Assert.Equal(RestartHandoffFailureKind.HelperCrashed, failure.FailureKind);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task HelperCrashAfterCommitCannotRelaunchOrRemainResident()
    {
        await using var scenario = StartScenario("helper-crash-postcommit", 5_000);
        _ = await scenario.ReadExpectedAsync("Prepared").ConfigureAwait(true);
        await scenario.SendAsync("COMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Single(evidence, item => item.Type == "Committed");
        var crash = Assert.Single(evidence, item => item.Type == "HelperPostCommitCrash");
        Assert.Equal(RestartHandoffState.Committed, crash.State);
        Assert.Equal(RestartHandoffFailureKind.HelperCrashed, crash.FailureKind);
        Assert.Equal(0, crash.RelaunchAttempts);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.False(scenario.ForcedCleanup);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task CommitAfterRevokeAndRepeatedRevokeAreRejected()
    {
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));
        var lease = await RestartHandoffLease.PrepareAsync(
                CreateFixtureStartInfo("helper", "normal"),
                Environment.ProcessId,
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(true);

        await lease.RevokeAsync().ConfigureAwait(true);

        Assert.Equal(RestartHandoffState.Revoked, lease.State);
        Assert.Throws<InvalidOperationException>(lease.Commit);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(lease.RevokeAsync)
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationIsTypedAndCannotCommit()
    {
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(1));
        var deadline = budget.CreateRestartHandoffDeadline();
        var nonce = Enumerable.Range(0, RestartHandoffProtocol.NonceLength)
            .Select(value => checked((byte)value))
            .ToArray();
        var authorization = new CancellationBlockingStream();
        await using var authorizationScope = authorization.ConfigureAwait(true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RestartAuthorizationFrame.ReadAsync(
                    authorization,
                    deadline,
                    nonce,
                    cancellation.Token))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task CancellationDuringPreparationReapsCandidateAndRemainsCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RestartHandoffLease.PrepareAsync(
                    CreateFixtureStartInfo("hold"),
                    Environment.ProcessId,
                    TransitionBudget.Start(
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromSeconds(2)),
                    cancellation.Token))
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("empty", RestartHandoffFailureKind.CommitChannelClosed)]
    [InlineData("partial", RestartHandoffFailureKind.AuthorizationRejected)]
    [InlineData("replay", RestartHandoffFailureKind.AuthorizationRejected)]
    [InlineData("malformed", RestartHandoffFailureKind.AuthorizationRejected)]
    public async Task AuthorizationPayloadsFailClosedBeforeCommit(
        string shape,
        RestartHandoffFailureKind expectedFailure)
    {
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        var deadline = budget.CreateRestartHandoffDeadline();
        var nonce = Enumerable.Range(0, RestartHandoffProtocol.NonceLength)
            .Select(value => checked((byte)value))
            .ToArray();
        var frame = RestartAuthorizationFrame.Create(deadline, nonce);
        var payload = shape switch
        {
            "empty" => [],
            "partial" => frame[..(frame.Length / 2)],
            "replay" => [.. frame, .. frame],
            "malformed" => frame.Select((value, index) => index == 0
                ? checked((byte)(value ^ 0xff))
                : value).ToArray(),
            _ => throw new InvalidOperationException("Unknown authorization shape.")
        };
        using var stream = new MemoryStream(payload);

        var result = await RestartAuthorizationFrame.ReadAsync(
                stream,
                deadline,
                nonce,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.NotNull(result);
        Assert.Equal(expectedFailure, result.Value.Kind);
    }

    [Fact]
    public async Task ExactWatcherDoesNotFollowAnUnrelatedExitedProcess()
    {
        var watcher = ParentLifetimeLeaseFactory.Create(Environment.ProcessId);
        await using var watcherScope = watcher.ConfigureAwait(false);
        using var unrelated = Process.Start(CreateFixtureStartInfo("instant-exit"))
            ?? throw new InvalidOperationException("The unrelated fixture did not start.");
        await unrelated.WaitForExitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(watcher.IsExited());
        Assert.Equal(ExpectedAuthority(), watcher.IdentityAuthority);
    }

    private static ScenarioSession StartScenario(string scenario, int windowMilliseconds)
    {
        return ScenarioSession.Start(
            typeof(FixtureMarker).Assembly.Location,
            scenario,
            windowMilliseconds);
    }

    private static ProcessIdentityAuthority ExpectedAuthority()
    {
        if (OperatingSystem.IsWindows())
        {
            return ProcessIdentityAuthority.WindowsProcessHandle;
        }

        if (OperatingSystem.IsLinux())
        {
            return ProcessIdentityAuthority.LinuxPidFd;
        }

        if (OperatingSystem.IsMacOS())
        {
            return ProcessIdentityAuthority.MacOSKqueueProcessNote;
        }

        throw new PlatformNotSupportedException();
    }

    private static ProcessStartInfo CreateFixtureStartInfo(params string[] arguments)
    {
        var fixturePath = typeof(FixtureMarker).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(fixturePath)
                ?? throw new InvalidOperationException("The fixture directory is unavailable.")
        };
        startInfo.ArgumentList.Add(fixturePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json instantiates this typed cross-process evidence record.")]
    private sealed record ProductionRestartEvidenceDto(
        string Type,
        RestartHandoffState State,
        RestartHandoffFailureKind? FailureKind,
        ProcessIdentityAuthority? Authority,
        int? ProcessId,
        int RelaunchAttempts,
        string? Detail,
        bool? Succeeded,
        IReadOnlyList<RestartHandoffCleanupFailure>? CleanupFailures);

    private sealed class ScenarioSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private bool _completed;

        private ScenarioSession(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        public bool ParentHasExited => _process.HasExited;

        public bool StandardOutputReachedEof { get; private set; }

        public bool ForcedCleanup { get; private set; }

        public static ScenarioSession Start(
            string fixturePath,
            string scenario,
            int windowMilliseconds)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(fixturePath)
                    ?? throw new InvalidOperationException("The fixture directory is unavailable."),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(fixturePath);
            startInfo.ArgumentList.Add("parent");
            startInfo.ArgumentList.Add(scenario);
            startInfo.ArgumentList.Add(windowMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The production restart parent fixture did not start.");
            return new ScenarioSession(process);
        }

        public async Task SendAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public async Task<ProductionRestartEvidenceDto> ReadExpectedAsync(string expectedType)
        {
            var evidence = await ReadNextAsync().ConfigureAwait(false);
            Assert.NotNull(evidence);
            Assert.Equal(expectedType, evidence.Type);
            return evidence;
        }

        public async Task<IReadOnlyList<ProductionRestartEvidenceDto>> CompleteAsync()
        {
            var evidence = new List<ProductionRestartEvidenceDto>();
            while (true)
            {
                var next = await ReadNextAsync().ConfigureAwait(false);
                if (next == null)
                {
                    break;
                }

                evidence.Add(next);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var error = await _standardError.ConfigureAwait(false);
            Assert.True(
                _process.ExitCode == 0,
                $"Fixture exited with {_process.ExitCode}. stderr: {error}");
            StandardOutputReachedEof = true;
            _completed = true;
            return evidence;
        }

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Failure-only fixture cleanup must not replace the causal test assertion.")]
        public async ValueTask DisposeAsync()
        {
            if (!_completed && !_process.HasExited)
            {
                ForcedCleanup = true;
                try
                {
                    _process.StandardInput.Close();
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _process.Dispose();
        }

        private async Task<ProductionRestartEvidenceDto?> ReadNextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line == null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProductionRestartEvidenceDto>(line, JsonOptions)
                ?? throw new InvalidOperationException($"Fixture evidence was empty: {line}");
        }
    }

    private sealed class CancellationBlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
