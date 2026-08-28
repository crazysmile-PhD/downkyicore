using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DownKyi.RestartHandoff.Fixture;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class RestartHandoffFeasibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CommittedExactParentExitRelaunchesOnceAndClosesHelper()
    {
        await using var scenario = StartScenario("normal", 4_000);
        var parent = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        var ready = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        Assert.Equal("Prepared", parent.State);
        Assert.Equal("Ready", ready.State);
        Assert.Equal(ExpectedAuthority(), ready.Authority);
        Assert.Equal(parent.ParentProcessId, ready.ParentProcessId);
        Assert.Equal(parent.PreparedDeadline, ready.PreparedDeadline);
        Assert.True(ready.RemainingTicks > 0);

        await scenario.SendAsync("VALID").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        AssertEventOrder(
            evidence,
            "Authorized",
            "ParentExitObserved",
            "RelaunchAttempted",
            "ReplacementStarted",
            "HelperTerminal");
        Assert.Single(evidence, item => item.Type == "RelaunchAttempted");
        Assert.Single(evidence, item => item.Type == "ReplacementStarted");
        var terminal = Assert.Single(evidence, item => item.Type == "HelperTerminal");
        Assert.Equal(1, terminal.RelaunchAttempts);
        Assert.Equal("Completed", terminal.Outcome);
        Assert.True(scenario.StandardOutputReachedEof);
        Assert.False(scenario.ForcedCleanup);
    }

    [Fact]
    public async Task StaleParentIdentityIsRejectedBeforeReady()
    {
        await using var scenario = StartScenario("stale-identity", 3_000);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Contains(evidence, item => item.Type == "WatcherRejected");
        Assert.DoesNotContain(evidence, item => item.Type == "WatcherReady");
        AssertNoRelaunch(evidence);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Theory]
    [InlineData("EOF", "AuthorizationEof")]
    [InlineData("PARTIAL", "PartialAuthorization")]
    [InlineData("REPLAY", "ReplayedAuthorization")]
    public async Task InvalidAuthorizationFailsClosed(string command, string expectedOutcome)
    {
        await using var scenario = StartScenario("authorization", 3_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync(command).ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var rejected = Assert.Single(evidence, item => item.Type == "AuthorizationRejected");
        Assert.Equal("Terminal", rejected.State);
        Assert.Equal(expectedOutcome, rejected.Outcome);
        AssertNoRelaunch(evidence);
    }

    [Fact]
    public async Task ParentExitBeforeCommitCannotAuthorizeRelaunch()
    {
        await using var scenario = StartScenario("precommit-exit", 3_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync("EXIT_PRECOMMIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var rejected = Assert.Single(evidence, item => item.Type == "AuthorizationRejected");
        Assert.Equal("AuthorizationEof", rejected.Outcome);
        AssertNoRelaunch(evidence);
    }

    [Fact]
    public async Task CommittedParentHangConsumesAuthenticatedDeadline()
    {
        await using var scenario = StartScenario("parent-hang", 1_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        var ready = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);
        await scenario.SendAsync("VALID_HOLD").ConfigureAwait(true);
        var authorized = await scenario.ReadExpectedAsync("Authorized").ConfigureAwait(true);
        var deadline = await scenario.ReadExpectedAsync("DeadlineExceeded").ConfigureAwait(true);

        Assert.Equal(ready.PreparedDeadline, authorized.PreparedDeadline);
        Assert.Equal(ready.PreparedDeadline, deadline.PreparedDeadline);
        Assert.Equal("DeadlineExceeded", deadline.Outcome);
        Assert.Equal(0, deadline.RelaunchAttempts);
        Assert.False(scenario.ParentHasExited);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var remaining = await scenario.CompleteAsync().ConfigureAwait(true);
        AssertNoRelaunch(remaining);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task DeadlineExhaustionBeforeCommitFailsClosed()
    {
        await using var scenario = StartScenario("deadline-before-commit", 700);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync("EXHAUST").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var rejected = Assert.Single(evidence, item => item.Type == "AuthorizationRejected");
        Assert.Equal("DeadlineExhaustedBeforeCommit", rejected.Outcome);
        AssertNoRelaunch(evidence);
    }

    [Fact]
    public async Task HelperCrashIsObservedWithoutRelaunch()
    {
        await using var scenario = StartScenario("helper-crash", 3_000);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Contains(evidence, item => item.Type == "WatcherReady");
        var crash = Assert.Single(evidence, item => item.Type == "HelperCrashObserved");
        Assert.Equal("73", crash.Outcome);
        AssertNoRelaunch(evidence);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task RelaunchStartFailureIsOneShotAndTerminal()
    {
        await using var scenario = StartScenario("relaunch-failure", 3_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync("VALID").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        var attempted = Assert.Single(evidence, item => item.Type == "RelaunchAttempted");
        var failed = Assert.Single(evidence, item => item.Type == "RelaunchFailed");
        Assert.Equal(1, attempted.RelaunchAttempts);
        Assert.Equal(1, failed.RelaunchAttempts);
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task NumericPidReuseCannotRedirectAnArmedExactWatcher()
    {
        await using var scenario = StartScenario("pid-reuse", 4_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        var ready = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);
        Assert.Equal(ExpectedAuthority(), ready.Authority);

        await scenario.SendAsync("VALID_HOLD").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("Authorized").ConfigureAwait(true);
        var reuse = await scenario.ReadExpectedAsync("NumericReuseIgnored").ConfigureAwait(true);
        Assert.Equal("Committed", reuse.State);
        Assert.False(scenario.ParentHasExited);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);
        AssertEventOrder(
            evidence,
            "ParentExitObserved",
            "RelaunchAttempted",
            "ReplacementStarted",
            "HelperTerminal");
    }

    [Fact]
    public async Task NumericPidAuthorityMutationIsRejectedWhileExactParentLives()
    {
        await using var scenario = StartScenario("numeric-authority", 3_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        var ready = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);
        Assert.Equal("NumericPidMutation", ready.Authority);

        await scenario.SendAsync("VALID_HOLD").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("Authorized").ConfigureAwait(true);
        var rejected = await scenario.ReadExpectedAsync("NumericAuthorityMutationRejected")
            .ConfigureAwait(true);
        Assert.Equal("FalseParentExitWhileExactParentAlive", rejected.Outcome);
        Assert.False(scenario.ParentHasExited);
        Assert.Equal(0, rejected.RelaunchAttempts);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);
        AssertNoRelaunch(evidence);
    }

    [Fact]
    public async Task ParentConsumptionReducesTheSameCrossProcessDeadline()
    {
        await using var scenario = StartScenario("consume-deadline", 4_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        var ready = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync("CONSUME:400").ConfigureAwait(true);
        var authorized = await scenario.ReadExpectedAsync("Authorized").ConfigureAwait(true);
        Assert.Equal(ready.PreparedDeadline, authorized.PreparedDeadline);
        Assert.True(authorized.ObservedTimestamp > ready.ObservedTimestamp);
        Assert.True(authorized.RemainingTicks < ready.RemainingTicks);
        Assert.True(
            authorized.ObservedTimestamp - ready.ObservedTimestamp >=
            Stopwatch.Frequency * 300L / 1000L);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);
        Assert.Contains(evidence, item => item.Type == "HelperTerminal");
    }

    [Fact]
    public async Task FreshHelperClockMutationCannotReplaceAuthenticatedExpiry()
    {
        await using var scenario = StartScenario("fresh-clock", 4_000);
        _ = await scenario.ReadExpectedAsync("ParentStarted").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("WatcherReady").ConfigureAwait(true);

        await scenario.SendAsync("CONSUME:300").ConfigureAwait(true);
        _ = await scenario.ReadExpectedAsync("Authorized").ConfigureAwait(true);
        var rejected = await scenario.ReadExpectedAsync("DeadlineMutationRejected")
            .ConfigureAwait(true);
        Assert.Equal("FreshClockWouldExtendAuthenticatedDeadline", rejected.Outcome);
        Assert.True(rejected.ObservedTimestamp > rejected.PreparedDeadline);
        Assert.True(rejected.RemainingTicks > 0);
        Assert.Equal(0, rejected.RelaunchAttempts);

        await scenario.SendAsync("EXIT").ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);
        AssertNoRelaunch(evidence);
    }

    [Fact]
    public async Task WatcherArmedAfterParentExitMutationCannotReachReady()
    {
        await using var scenario = StartScenario("late-watcher", 2_000);
        await scenario.ReleaseLateWatcherAfterParentExitAsync().ConfigureAwait(true);
        var evidence = await scenario.CompleteAsync().ConfigureAwait(true);

        Assert.Contains(evidence, item => item.Type == "WatcherRejected");
        Assert.DoesNotContain(evidence, item => item.Type == "WatcherReady");
        AssertNoRelaunch(evidence);
        Assert.True(scenario.StandardOutputReachedEof);
    }

    [Fact]
    public async Task OrdinaryOwnerDeathLeaseCannotSpanCommittedSuccessorTransition()
    {
        var pipeName = IpcEndpointName.Create("Stage4A.OrdinaryLeaseRegression");
        using var pipe = new NamedPipeServerStream(
            pipeName.PhysicalIdentifier,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var fixturePath = typeof(FixtureMarker).Assembly.Location;
        var launchSpec = new LaunchSpec(
            "dotnet",
            new[] { fixturePath, "owned-successor", pipeName.PhysicalIdentifier },
            Path.GetDirectoryName(fixturePath)
                ?? throw new InvalidOperationException("The fixture directory is unavailable."));
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
                await ReadLineWithSafetyDeadlineAsync(reader).ConfigureAwait(true));
            Assert.Equal("Ready", ready.State);
            Assert.False(ready.Committed);
            await writer.WriteLineAsync("COMMIT").ConfigureAwait(true);
            var committed = DeserializeOwnedSuccessor(
                await ReadLineWithSafetyDeadlineAsync(reader).ConfigureAwait(true));
            Assert.Equal("Committed", committed.State);
            Assert.True(committed.Committed);
            Assert.Equal(0, committed.RelaunchAttempts);
        }

        lease.CloseOwnerLifetimeForTesting();
        var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                () => lease.WaitAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Equal(OwnedProcessFailureKind.ExecutionFailed, failure.Failure.Kind);
        Assert.Empty(failure.CleanupFailures);
        Assert.Null(await ReadLineWithSafetyDeadlineAsync(reader, allowEof: true)
            .ConfigureAwait(true));
    }

    private static ScenarioSession StartScenario(string scenario, int windowMilliseconds)
    {
        return ScenarioSession.Start(
            typeof(FixtureMarker).Assembly.Location,
            scenario,
            windowMilliseconds);
    }

    private static string ExpectedAuthority()
    {
        if (OperatingSystem.IsWindows())
        {
            return "WindowsProcessHandle";
        }

        if (OperatingSystem.IsLinux())
        {
            return "LinuxPidFd";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "MacOsKqueueProcessNote";
        }

        throw new PlatformNotSupportedException();
    }

    private static void AssertNoRelaunch(IEnumerable<RestartEvidenceDto> evidence)
    {
        Assert.DoesNotContain(evidence, item => item.Type == "RelaunchAttempted");
        Assert.DoesNotContain(evidence, item => item.Type == "ReplacementStarted");
    }

    private static void AssertEventOrder(
        IReadOnlyList<RestartEvidenceDto> evidence,
        params string[] expectedTypes)
    {
        var previous = -1;
        foreach (var expectedType in expectedTypes)
        {
            var index = -1;
            for (var candidate = previous + 1; candidate < evidence.Count; candidate++)
            {
                if (string.Equals(
                        evidence[candidate].Type,
                        expectedType,
                        StringComparison.Ordinal))
                {
                    index = candidate;
                    break;
                }
            }

            Assert.True(index > previous, $"Event '{expectedType}' was missing or out of order.");
            previous = index;
        }
    }

    private static OwnedSuccessorEvidenceDto DeserializeOwnedSuccessor(string? line)
    {
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<OwnedSuccessorEvidenceDto>(line, JsonOptions)
            ?? throw new InvalidOperationException("Owned successor evidence was empty.");
    }

    private static async Task<string?> ReadLineWithSafetyDeadlineAsync(
        StreamReader reader,
        bool allowEof = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true);
        if (!allowEof && line == null)
        {
            throw new EndOfStreamException("The fixture evidence pipe closed early.");
        }

        return line;
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json instantiates this typed cross-process evidence record.")]
    private sealed record RestartEvidenceDto(
        string Type,
        string State,
        string Platform,
        string? Authority,
        int ParentProcessId,
        int HelperProcessId,
        long PreparedDeadline,
        long ObservedTimestamp,
        long RemainingTicks,
        int RelaunchAttempts,
        string? Outcome,
        string? Mutation);

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json instantiates this typed cross-process evidence record.")]
    private sealed record OwnedSuccessorEvidenceDto(
        string State,
        bool Committed,
        int RelaunchAttempts);

    private sealed class ScenarioSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private readonly NamedPipeServerStream? _lateWatcherGate;
        private bool _completed;

        private ScenarioSession(Process process, NamedPipeServerStream? lateWatcherGate)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
            _lateWatcherGate = lateWatcherGate;
        }

        public bool ParentHasExited => _process.HasExited;

        public bool StandardOutputReachedEof { get; private set; }

        public bool ForcedCleanup { get; private set; }

        public static ScenarioSession Start(
            string fixturePath,
            string scenario,
            int windowMilliseconds)
        {
            var lateWatcherPipeName = scenario == "late-watcher"
                ? IpcEndpointName.Create("Stage4A.LateWatcherRelease")
                : default;
            var lateWatcherGate = scenario == "late-watcher"
                ? new NamedPipeServerStream(
                    lateWatcherPipeName!.PhysicalIdentifier,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous)
                : null;
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
            startInfo.ArgumentList.Add(scenario == "late-watcher"
                ? lateWatcherPipeName!.PhysicalIdentifier
                : "-");
            try
            {
                var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "The restart handoff parent fixture did not start.");
                return new ScenarioSession(process, lateWatcherGate);
            }
            catch
            {
                lateWatcherGate?.Dispose();
                throw;
            }
        }

        public async Task ReleaseLateWatcherAfterParentExitAsync()
        {
            Assert.NotNull(_lateWatcherGate);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _lateWatcherGate.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await _lateWatcherGate.WriteAsync(new byte[] { 1 }, timeout.Token)
                .ConfigureAwait(false);
            await _lateWatcherGate.FlushAsync(timeout.Token).ConfigureAwait(false);
            await _lateWatcherGate.DisposeAsync().ConfigureAwait(false);
        }

        public async Task SendAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public async Task<RestartEvidenceDto> ReadExpectedAsync(string expectedType)
        {
            var evidence = await ReadNextAsync().ConfigureAwait(false);
            Assert.NotNull(evidence);
            Assert.Equal(expectedType, evidence.Type);
            return evidence;
        }

        public async Task<IReadOnlyList<RestartEvidenceDto>> CompleteAsync()
        {
            var evidence = new List<RestartEvidenceDto>();
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
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
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

            _lateWatcherGate?.Dispose();
            _process.Dispose();
        }

        private async Task<RestartEvidenceDto?> ReadNextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line == null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<RestartEvidenceDto>(line, JsonOptions)
                ?? throw new InvalidOperationException($"Fixture evidence was empty: {line}");
        }
    }
}
