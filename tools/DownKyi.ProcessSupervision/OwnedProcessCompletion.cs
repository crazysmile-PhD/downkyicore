namespace DownKyi.ProcessSupervision;

internal sealed record OwnedProcessProofSnapshot(
    IReadOnlyList<OwnedProcessInvariantResult> Invariants,
    IReadOnlyList<OwnedProcessFact> Facts,
    IReadOnlyList<OwnedProcessFailure> Failures)
{
    internal bool FormalGatePassed =>
        Invariants.All(invariant => invariant.State == OwnedProcessInvariantState.Proven);
}

internal sealed class OwnedProcessProofAccumulator
{
    private readonly object _sync = new();
    private readonly Dictionary<OwnedProcessInvariantKind, OwnedProcessInvariantState> _states =
        Enum.GetValues<OwnedProcessInvariantKind>()
            .ToDictionary(
                invariant => invariant,
                _ => OwnedProcessInvariantState.Unknown);
    private readonly List<OwnedProcessFact> _facts = [];
    private readonly List<OwnedProcessFailure> _failures = [];

    internal void Prove(OwnedProcessInvariantKind invariant)
    {
        lock (_sync)
        {
            if (_states[invariant] != OwnedProcessInvariantState.Violated)
            {
                _states[invariant] = OwnedProcessInvariantState.Proven;
            }
        }
    }

    internal void Violate(
        OwnedProcessInvariantKind invariant,
        OwnedProcessFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_sync)
        {
            _states[invariant] = OwnedProcessInvariantState.Violated;
            _failures.Add(failure);
        }
    }

    internal void RecordFact(OwnedProcessFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        lock (_sync)
        {
            _facts.Add(fact);
        }
    }

    internal void RecordFailure(OwnedProcessFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_sync)
        {
            _failures.Add(failure);
        }
    }

    internal OwnedProcessProofSnapshot Snapshot()
    {
        lock (_sync)
        {
            var invariants = _states
                .OrderBy(entry => entry.Key)
                .Select(entry => new OwnedProcessInvariantResult(entry.Key, entry.Value))
                .ToArray();
            var facts = _facts
                .Distinct()
                .OrderBy(fact => fact.Kind)
                .ThenBy(fact => fact.Phase)
                .ThenBy(fact => fact.Detail, StringComparer.Ordinal)
                .ToArray();
            var failures = _failures
                .Distinct()
                .OrderBy(failure => failure.Kind)
                .ThenBy(failure => failure.Phase)
                .ThenBy(failure => failure.Channel)
                .ThenBy(failure => failure.ErrorType, StringComparer.Ordinal)
                .ThenBy(failure => failure.Message, StringComparer.Ordinal)
                .ToArray();
            return new OwnedProcessProofSnapshot(invariants, facts, failures);
        }
    }
}

internal readonly record struct OwnedProcessWaitDecision(
    Task<OwnedProcessOutcome> Completion,
    bool StartsOwner);

internal readonly record struct OwnedProcessLifetimeCloseDecision(
    Task<OwnedProcessOutcome> Completion,
    bool StartsOwner,
    bool SignalOwner);

internal sealed class OwnedProcessCompletionGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<OwnedProcessOutcome> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private OwnedProcessCompletionState _state;
    private bool _waitRegistered;
    private bool _lifetimeCloseRequested;

    internal Task<OwnedProcessOutcome> Completion => _completion.Task;

    internal bool LifetimeCloseRequested
    {
        get
        {
            lock (_sync)
            {
                return _lifetimeCloseRequested;
            }
        }
    }

    internal OwnedProcessWaitDecision BeginWait()
    {
        lock (_sync)
        {
            if (_waitRegistered)
            {
                throw new InvalidOperationException(
                    "The owned-process wait has already been registered.");
            }

            _waitRegistered = true;
            var startsOwner = _state == OwnedProcessCompletionState.Available;
            if (startsOwner)
            {
                _state = OwnedProcessCompletionState.Active;
            }

            return new OwnedProcessWaitDecision(Completion, startsOwner);
        }
    }

    internal OwnedProcessLifetimeCloseDecision RequestLifetimeClose()
    {
        lock (_sync)
        {
            var firstRequest = !_lifetimeCloseRequested;
            _lifetimeCloseRequested = true;
            var startsOwner = _state == OwnedProcessCompletionState.Available;
            if (startsOwner)
            {
                _state = OwnedProcessCompletionState.Active;
            }

            return new OwnedProcessLifetimeCloseDecision(
                Completion,
                startsOwner,
                SignalOwner: firstRequest && !startsOwner &&
                    _state == OwnedProcessCompletionState.Active);
        }
    }

    internal bool TryPublish(OwnedProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_sync)
        {
            if (_state == OwnedProcessCompletionState.Completed)
            {
                return false;
            }

            if (_state != OwnedProcessCompletionState.Active)
            {
                throw new InvalidOperationException(
                    "Only the active owned-process owner can publish terminal proof.");
            }

            _state = OwnedProcessCompletionState.Completed;
            if (!_completion.TrySetResult(outcome))
            {
                throw new InvalidOperationException(
                    "The owned-process terminal proof was already published.");
            }

            return true;
        }
    }

    private enum OwnedProcessCompletionState
    {
        Available,
        Active,
        Completed
    }
}
