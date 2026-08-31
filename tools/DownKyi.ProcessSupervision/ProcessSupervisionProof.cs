using System.Collections.ObjectModel;

#pragma warning disable CA1515 // These immutable contracts are consumed outside this assembly.

namespace DownKyi.ProcessSupervision;

public enum RequiredProcessInvariantKind
{
    TargetTerminal,
    RequiredContainment,
    OperationCompletion,
    OperationBudget,
    TreeQuiescence,
    BoundedCleanup,
    StreamDrain,
    OwnershipLifetime
}

public enum ProcessInvariantState
{
    Unknown,
    Proven,
    Violated
}

public sealed record ProcessInvariantEvidence
{
    internal ProcessInvariantEvidence(
        RequiredProcessInvariantKind kind,
        ProcessInvariantState state,
        string detail)
    {
        if (state == ProcessInvariantState.Unknown)
        {
            throw new ArgumentException(
                "Unknown invariant state cannot be represented as evidence.",
                nameof(state));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Kind = kind;
        State = state;
        Detail = detail;
    }

    public RequiredProcessInvariantKind Kind { get; }

    public ProcessInvariantState State { get; }

    public string Detail { get; }
}

public sealed record ProcessInvariantResult
{
    internal ProcessInvariantResult(
        RequiredProcessInvariantKind kind,
        ProcessInvariantState state,
        IEnumerable<ProcessInvariantEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var snapshot = evidence.ToArray();
        if (!HasMatchingEvidence(kind, state, snapshot))
        {
            throw new ArgumentException(
                "Invariant evidence kind and state must exactly match the result; unknown state forbids evidence.",
                nameof(evidence));
        }

        Kind = kind;
        State = state;
        Evidence = new ReadOnlyCollection<ProcessInvariantEvidence>(snapshot);
    }

    public RequiredProcessInvariantKind Kind { get; }

    public ProcessInvariantState State { get; }

    public IReadOnlyList<ProcessInvariantEvidence> Evidence { get; }

    internal bool HasConsistentEvidence => HasMatchingEvidence(
        Kind,
        State,
        Evidence);

    private static bool HasMatchingEvidence(
        RequiredProcessInvariantKind kind,
        ProcessInvariantState state,
        IReadOnlyCollection<ProcessInvariantEvidence> evidence)
    {
        return state == ProcessInvariantState.Unknown
            ? evidence.Count == 0
            : evidence.Count > 0 && evidence.All(item =>
                item.Kind == kind && item.State == state);
    }
}

public sealed class ProcessSupervisionProof
{
    internal ProcessSupervisionProof(
        IEnumerable<ProcessInvariantResult> invariantResults)
    {
        ArgumentNullException.ThrowIfNull(invariantResults);
        var snapshot = invariantResults.ToArray();
        var required = Enum.GetValues<RequiredProcessInvariantKind>();
        if (snapshot.Any(result =>
                result is null || !result.HasConsistentEvidence) ||
            snapshot.Length != required.Length ||
            required.Any(kind => snapshot.Count(result => result.Kind == kind) != 1))
        {
            throw new ArgumentException(
                "Process proof requires consistent evidence and every required invariant exactly once.",
                nameof(invariantResults));
        }

        Invariants = new ReadOnlyCollection<ProcessInvariantResult>(
            snapshot.OrderBy(result => result.Kind).ToArray());
    }

    public IReadOnlyList<ProcessInvariantResult> Invariants { get; }

    public bool FormalGatePassed => Invariants.All(result =>
        result.State == ProcessInvariantState.Proven &&
        result.HasConsistentEvidence);
}

internal sealed class ProcessProofBuilder
{
    private readonly Dictionary<RequiredProcessInvariantKind, ProcessInvariantState> _states =
        Enum.GetValues<RequiredProcessInvariantKind>()
            .ToDictionary(kind => kind, _ => ProcessInvariantState.Unknown);
    private readonly Dictionary<RequiredProcessInvariantKind, List<ProcessInvariantEvidence>> _evidence =
        Enum.GetValues<RequiredProcessInvariantKind>()
            .ToDictionary(kind => kind, _ => new List<ProcessInvariantEvidence>());

    internal void Prove(RequiredProcessInvariantKind kind, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (_states[kind] == ProcessInvariantState.Violated)
        {
            return;
        }

        _states[kind] = ProcessInvariantState.Proven;
        _evidence[kind].Add(new ProcessInvariantEvidence(
            kind,
            ProcessInvariantState.Proven,
            detail));
    }

    internal void Violate(RequiredProcessInvariantKind kind, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (_states[kind] != ProcessInvariantState.Violated)
        {
            _evidence[kind].Clear();
        }

        _states[kind] = ProcessInvariantState.Violated;
        _evidence[kind].Add(new ProcessInvariantEvidence(
            kind,
            ProcessInvariantState.Violated,
            detail));
    }

    internal ProcessSupervisionProof Build()
    {
        return new ProcessSupervisionProof(
            Enum.GetValues<RequiredProcessInvariantKind>()
                .Select(kind => new ProcessInvariantResult(
                    kind,
                    _states[kind],
                    _evidence[kind])));
    }
}
