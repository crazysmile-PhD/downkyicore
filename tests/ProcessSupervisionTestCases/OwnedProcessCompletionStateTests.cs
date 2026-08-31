using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

public sealed class OwnedProcessCompletionStateTests
{
    [Fact]
    public void DiagnosticPayloadCannotChangeFormalGateResult()
    {
        var proof = new OwnedProcessProofAccumulator();
        foreach (var invariant in Enum.GetValues<OwnedProcessInvariantKind>())
        {
            proof.Prove(invariant);
        }
        var snapshot = proof.Snapshot();
        var ownership = new ProcessOwnershipMetadata(
            ProcessIdentityAuthority.DirectChildWait,
            ProcessContainmentKind.LinuxProcessGroup,
            ProcessContainmentStrength.TrustedChildProcessGroup,
            ProcessMembershipAuthority.LinuxProcessGroupSignal,
            "containment",
            "membership",
            "owner",
            OwnershipEstablished: true);

        var quiet = new OwnedProcessOutcome(
            1, 2, 0, string.Empty, string.Empty, null, ownership,
            snapshot.Invariants, snapshot.Facts, snapshot.Failures);
        var noisy = new OwnedProcessOutcome(
            1, 2, 0, "diagnostic stdout", "diagnostic stderr", 123, ownership,
            snapshot.Invariants, snapshot.Facts, snapshot.Failures);

        Assert.True(quiet.FormalGatePassed);
        Assert.Equal(quiet.FormalGatePassed, noisy.FormalGatePassed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ViolationAbsorbsProofRegardlessOfSubmissionOrder(bool proofFirst)
    {
        var accumulator = new OwnedProcessProofAccumulator();
        var failure = Failure(OwnedProcessFailureKind.OwnedTreeNotQuiescent);

        if (proofFirst)
        {
            accumulator.Prove(OwnedProcessInvariantKind.TreeQuiescence);
            accumulator.Violate(OwnedProcessInvariantKind.TreeQuiescence, failure);
        }
        else
        {
            accumulator.Violate(OwnedProcessInvariantKind.TreeQuiescence, failure);
            accumulator.Prove(OwnedProcessInvariantKind.TreeQuiescence);
        }

        var snapshot = accumulator.Snapshot();
        Assert.Equal(
            OwnedProcessInvariantState.Violated,
            State(snapshot, OwnedProcessInvariantKind.TreeQuiescence));
        Assert.Equal([failure], snapshot.Failures);
        Assert.False(snapshot.FormalGatePassed);
    }

    [Fact]
    public void SameFactsProduceSameSortedSnapshotAcrossCallbackSchedules()
    {
        var forward = BuildSnapshot(reverse: false);
        var reverse = BuildSnapshot(reverse: true);

        Assert.Equal(forward.Invariants, reverse.Invariants);
        Assert.Equal(forward.Facts, reverse.Facts);
        Assert.Equal(forward.Failures, reverse.Failures);
        Assert.Equal(forward.FormalGatePassed, reverse.FormalGatePassed);
    }

    [Fact]
    public void CleanupFailureCannotEraseEarlierAuthoritativeEvidence()
    {
        var accumulator = new OwnedProcessProofAccumulator();
        var terminal = new OwnedProcessFact(
            OwnedProcessFactKind.TargetTerminal,
            OwnedProcessFailurePhase.TargetExecution,
            "exit=7");
        accumulator.RecordFact(terminal);
        accumulator.Prove(OwnedProcessInvariantKind.TargetTerminal);
        accumulator.Violate(
            OwnedProcessInvariantKind.BoundedCleanup,
            Failure(
                OwnedProcessFailureKind.TerminationFailed,
                OwnedProcessFailureChannel.Cleanup));

        var snapshot = accumulator.Snapshot();

        Assert.Contains(terminal, snapshot.Facts);
        Assert.Equal(
            OwnedProcessInvariantState.Proven,
            State(snapshot, OwnedProcessInvariantKind.TargetTerminal));
        Assert.Equal(
            OwnedProcessInvariantState.Violated,
            State(snapshot, OwnedProcessInvariantKind.BoundedCleanup));
    }

    [Fact]
    public void UnknownRequiredInvariantFailsClosed()
    {
        var accumulator = new OwnedProcessProofAccumulator();
        foreach (var invariant in Enum.GetValues<OwnedProcessInvariantKind>()
                     .Where(invariant => invariant != OwnedProcessInvariantKind.StreamDrain))
        {
            accumulator.Prove(invariant);
        }

        var snapshot = accumulator.Snapshot();

        Assert.Equal(
            OwnedProcessInvariantState.Unknown,
            State(snapshot, OwnedProcessInvariantKind.StreamDrain));
        Assert.False(snapshot.FormalGatePassed);
    }

    [Fact]
    public void DuplicateProvenInvariantCannotHideMissingRequiredInvariant()
    {
        var invariants = Enum.GetValues<OwnedProcessInvariantKind>()
            .Where(kind => kind != OwnedProcessInvariantKind.StreamDrain)
            .Select(kind => new OwnedProcessInvariantResult(
                kind,
                OwnedProcessInvariantState.Proven))
            .Append(new OwnedProcessInvariantResult(
                OwnedProcessInvariantKind.TargetTerminal,
                OwnedProcessInvariantState.Proven));
        var outcome = new OwnedProcessOutcome(
            1,
            2,
            0,
            string.Empty,
            string.Empty,
            null,
            Ownership,
            invariants,
            [],
            []);

        Assert.False(outcome.FormalGatePassed);
    }

    [Fact]
    public async Task WaitAndLifetimeCloseShareOneCompletionWriter()
    {
        var gate = new OwnedProcessCompletionGate();
        var wait = gate.BeginWait();
        var close = gate.RequestLifetimeClose();
        var outcome = Outcome(allProven: false);

        Assert.True(wait.StartsOwner);
        Assert.False(close.StartsOwner);
        Assert.True(close.SignalOwner);
        Assert.True(gate.LifetimeCloseRequested);
        Assert.True(gate.TryPublish(outcome));
        Assert.False(gate.TryPublish(Outcome(allProven: true)));
        Assert.Same(outcome, await wait.Completion.ConfigureAwait(true));
        Assert.Same(outcome, await close.Completion.ConfigureAwait(true));
    }

    [Fact]
    public async Task LifetimeCloseBeforeWaitStartsTheSameOwner()
    {
        var gate = new OwnedProcessCompletionGate();
        var close = gate.RequestLifetimeClose();
        var wait = gate.BeginWait();
        var outcome = Outcome(allProven: false);

        Assert.True(close.StartsOwner);
        Assert.False(close.SignalOwner);
        Assert.False(wait.StartsOwner);
        Assert.True(gate.TryPublish(outcome));
        Assert.Same(outcome, await wait.Completion.ConfigureAwait(true));
    }

    private static OwnedProcessProofSnapshot BuildSnapshot(bool reverse)
    {
        var accumulator = new OwnedProcessProofAccumulator();
        var facts = new[]
        {
            new OwnedProcessFact(
                OwnedProcessFactKind.CancellationRequested,
                OwnedProcessFailurePhase.TargetExecution),
            new OwnedProcessFact(
                OwnedProcessFactKind.OperationDeadlineExceeded,
                OwnedProcessFailurePhase.TargetExecution),
            new OwnedProcessFact(
                OwnedProcessFactKind.TargetTerminal,
                OwnedProcessFailurePhase.TargetExecution,
                "exit=1")
        };
        var failures = new[]
        {
            Failure(OwnedProcessFailureKind.CallerCancelled),
            Failure(OwnedProcessFailureKind.OperationDeadlineExceeded)
        };
        foreach (var fact in reverse ? facts.Reverse() : facts)
        {
            accumulator.RecordFact(fact);
        }
        foreach (var failure in reverse ? failures.Reverse() : failures)
        {
            accumulator.RecordFailure(failure);
        }
        accumulator.Prove(OwnedProcessInvariantKind.TargetTerminal);
        accumulator.Violate(OwnedProcessInvariantKind.OperationCompletion, failures[0]);
        accumulator.Violate(OwnedProcessInvariantKind.OperationBudget, failures[1]);
        return accumulator.Snapshot();
    }

    private static OwnedProcessInvariantState State(
        OwnedProcessProofSnapshot snapshot,
        OwnedProcessInvariantKind invariant)
    {
        return Assert.Single(snapshot.Invariants, item => item.Kind == invariant).State;
    }

    private static OwnedProcessFailure Failure(
        OwnedProcessFailureKind kind,
        OwnedProcessFailureChannel channel = OwnedProcessFailureChannel.Operation)
    {
        return new OwnedProcessFailure(
            kind,
            OwnedProcessFailurePhase.TargetExecution,
            channel,
            nameof(InvalidOperationException),
            kind.ToString());
    }

    private static OwnedProcessOutcome Outcome(bool allProven)
    {
        var invariants = Enum.GetValues<OwnedProcessInvariantKind>()
            .Select(kind => new OwnedProcessInvariantResult(
                kind,
                allProven
                    ? OwnedProcessInvariantState.Proven
                    : OwnedProcessInvariantState.Unknown));
        return new OwnedProcessOutcome(
            1,
            2,
            0,
            string.Empty,
            string.Empty,
            1234,
            Ownership,
            invariants,
            [],
            []);
    }

    private static readonly ProcessOwnershipMetadata Ownership = new(
        ProcessIdentityAuthority.DirectChildWait,
        ProcessContainmentKind.LinuxProcessGroup,
        ProcessContainmentStrength.TrustedChildProcessGroup,
        ProcessMembershipAuthority.LinuxProcessGroupSignal,
        "containment",
        "membership",
        "owner",
        OwnershipEstablished: true);
}
