using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessSupervisionContractBehaviorTests
{
    [Fact]
    public void UnknownAndViolatedRequiredInvariantsFailClosed()
    {
        var unknownBuilder = ProveAllExcept(RequiredProcessInvariantKind.StreamDrain);
        var unknown = unknownBuilder.Build();

        var unknownStreamDrain = Assert.Single(
            unknown.Invariants,
            result => result.Kind == RequiredProcessInvariantKind.StreamDrain);
        Assert.Equal(ProcessInvariantState.Unknown, unknownStreamDrain.State);
        Assert.Empty(unknownStreamDrain.Evidence);
        Assert.False(unknown.FormalGatePassed);

        var violatedBuilder = ProveAll();
        violatedBuilder.Violate(
            RequiredProcessInvariantKind.TreeQuiescence,
            "descendant membership remained");
        violatedBuilder.Prove(
            RequiredProcessInvariantKind.TreeQuiescence,
            "late callback attempted to overwrite the violation");
        var violated = violatedBuilder.Build();

        var tree = Assert.Single(
            violated.Invariants,
            result => result.Kind == RequiredProcessInvariantKind.TreeQuiescence);
        Assert.Equal(ProcessInvariantState.Violated, tree.State);
        Assert.All(tree.Evidence, evidence =>
            Assert.Equal(ProcessInvariantState.Violated, evidence.State));
        Assert.False(violated.FormalGatePassed);
    }

    [Fact]
    public void ProofAlwaysContainsEveryRequiredInvariantAndRejectsOmission()
    {
        var proof = new ProcessProofBuilder().Build();

        Assert.Equal(
            Enum.GetValues<RequiredProcessInvariantKind>(),
            proof.Invariants.Select(result => result.Kind));
        Assert.All(proof.Invariants, result =>
            Assert.Equal(ProcessInvariantState.Unknown, result.State));
        Assert.False(proof.FormalGatePassed);

        Assert.Throws<ArgumentException>(() => new ProcessSupervisionProof(
            proof.Invariants.Where(result =>
                result.Kind != RequiredProcessInvariantKind.OwnershipLifetime)));
    }

    [Fact]
    public void ProofSnapshotCannotBeChangedByLaterBuilderTransitions()
    {
        var builder = ProveAllExcept(RequiredProcessInvariantKind.StreamDrain);
        var before = builder.Build();

        builder.Prove(
            RequiredProcessInvariantKind.StreamDrain,
            "both streams reached EOF");
        var after = builder.Build();

        Assert.False(before.FormalGatePassed);
        Assert.True(after.FormalGatePassed);
        Assert.Equal(
            ProcessInvariantState.Unknown,
            Assert.Single(before.Invariants, result =>
                result.Kind == RequiredProcessInvariantKind.StreamDrain).State);
    }

    [Fact]
    public void ProvenResultRejectsAnyViolatedEvidence()
    {
        var kind = RequiredProcessInvariantKind.OperationBudget;
        var proven = new ProcessInvariantEvidence(
            kind,
            ProcessInvariantState.Proven,
            "one monotonic authority retained");
        var violated = new ProcessInvariantEvidence(
            kind,
            ProcessInvariantState.Violated,
            "independent deadline observed");

        Assert.Throws<ArgumentException>(() => new ProcessInvariantResult(
            kind,
            ProcessInvariantState.Proven,
            [proven, violated]));
    }

    [Fact]
    public void TransitionBudgetDerivesBothDeadlinesFromOneMonotonicAuthority()
    {
        var clock = new ManualMonotonicTimeProvider();
        var budget = TransitionBudget.StartForTesting(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5),
            clock);

        Assert.Same(budget, budget.Operation.Authority);
        Assert.Same(budget, budget.Cleanup.Authority);
        Assert.Equal(TimeSpan.FromSeconds(10), budget.Operation.Limit);
        Assert.Equal(TimeSpan.FromSeconds(15), budget.Cleanup.Limit);

        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(6), budget.Operation.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(11), budget.Cleanup.Remaining);
        Assert.False(budget.Operation.IsExpired);
        Assert.False(budget.Cleanup.IsExpired);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.True(budget.Operation.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(5), budget.Cleanup.Remaining);
    }

    [Fact]
    public void SameAuthorityUsesAuthoritativeSequenceInsteadOfCandidateKind()
    {
        var laterContainmentFailure = Primary(
            ProcessPrimaryFailureKind.ContainmentFailure,
            authoritySequence: 2);
        var earlierExecutionFailure = Primary(
            ProcessPrimaryFailureKind.ExecutionFailure,
            authoritySequence: 1);

        var selected = ProcessTerminalSelectionPolicy.Select(
            [laterContainmentFailure, earlierExecutionFailure]);

        Assert.Same(earlierExecutionFailure, selected);
    }

    [Fact]
    public void DifferentAuthoritiesUseFixedPrecedenceIndependentOfInputOrder()
    {
        var target = ProcessTerminalCandidateFactory.TargetTerminal(3, 0);
        var execution = Primary(ProcessPrimaryFailureKind.ExecutionFailure, 2);
        var cancellation = Primary(ProcessPrimaryFailureKind.CallerCancellation, 1);
        var deadline = Primary(ProcessPrimaryFailureKind.DeadlineExceeded, 0);
        ProcessTerminalCandidate[] reverseArrival =
        [deadline, cancellation, execution, target];

        var forward = ProcessTerminalSelectionPolicy.Select(
            reverseArrival.Reverse());
        var reverse = ProcessTerminalSelectionPolicy.Select(reverseArrival);

        Assert.Same(target, forward);
        Assert.Same(target, reverse);
    }

    [Fact]
    public void AmbiguousSequenceWithinOneAuthorityFailsClosed()
    {
        var containment = Primary(
            ProcessPrimaryFailureKind.ContainmentFailure,
            authoritySequence: 7);
        var execution = Primary(
            ProcessPrimaryFailureKind.ExecutionFailure,
            authoritySequence: 7);

        Assert.Throws<InvalidOperationException>(() =>
            ProcessTerminalSelectionPolicy.Select([containment, execution]));
    }

    [Fact]
    public void LaterContradictionFailsClosedEvenWhenAuthorityHasEarlierWinner()
    {
        var earlier = Primary(
            ProcessPrimaryFailureKind.ExecutionFailure,
            authoritySequence: 1);
        var laterContainment = Primary(
            ProcessPrimaryFailureKind.ContainmentFailure,
            authoritySequence: 2);
        var laterExecution = Primary(
            ProcessPrimaryFailureKind.ExecutionFailure,
            authoritySequence: 2);
        ProcessTerminalCandidate[] publications =
        [earlier, laterContainment, laterExecution];

        Assert.Throws<InvalidOperationException>(() =>
            ProcessTerminalSelectionPolicy.Select(publications));
        Assert.Throws<InvalidOperationException>(() =>
            ProcessTerminalSelectionPolicy.Select(publications.Reverse()));
    }

    [Fact]
    public void CleanupFailureCannotOverwritePrimaryTerminalChannel()
    {
        var primary = Primary(
            ProcessPrimaryFailureKind.CallerCancellation,
            authoritySequence: 1);
        var cleanup = new List<ProcessCleanupFailure>
        {
            new(
                ProcessCleanupFailureKind.ResourceReleaseFailure,
                nameof(InvalidOperationException),
                "dispose failed")
        };

        var outcome = new ProcessSupervisionOutcome(
            primary,
            ProveAll().Build(),
            cleanup);
        cleanup.Clear();

        Assert.Same(primary, outcome.Terminal);
        Assert.Same(primary.PrimaryFailure, outcome.PrimaryFailure);
        Assert.Equal(
            ProcessPrimaryFailureKind.CallerCancellation,
            outcome.PrimaryFailure?.Kind);
        Assert.Equal(
            ProcessCleanupFailureKind.ResourceReleaseFailure,
            Assert.Single(outcome.CleanupFailures).Kind);
        Assert.True(outcome.Proof.FormalGatePassed);
    }

    private static ProcessTerminalCandidate Primary(
        ProcessPrimaryFailureKind kind,
        long authoritySequence)
    {
        return ProcessTerminalCandidateFactory.PrimaryFailure(
            kind,
            authoritySequence,
            nameof(InvalidOperationException),
            kind.ToString());
    }

    private static ProcessProofBuilder ProveAll()
    {
        var builder = new ProcessProofBuilder();
        foreach (var kind in Enum.GetValues<RequiredProcessInvariantKind>())
        {
            builder.Prove(kind, $"{kind} evidence");
        }

        return builder;
    }

    private static ProcessProofBuilder ProveAllExcept(
        RequiredProcessInvariantKind omitted)
    {
        var builder = new ProcessProofBuilder();
        foreach (var kind in Enum.GetValues<RequiredProcessInvariantKind>()
                     .Where(kind => kind != omitted))
        {
            builder.Prove(kind, $"{kind} evidence");
        }

        return builder;
    }

    private sealed class ManualMonotonicTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            throw new InvalidOperationException(
                "TransitionBudget must not read wall-clock time.");
        }

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            _timestamp = checked(_timestamp + elapsed.Ticks);
        }
    }
}
