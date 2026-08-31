using System.Collections.ObjectModel;

#pragma warning disable CA1515 // These immutable contracts are consumed outside this assembly.

namespace DownKyi.ProcessSupervision;

public enum ProcessTerminalCandidateKind
{
    TargetTerminal,
    ContainmentFailure,
    ExecutionFailure,
    CallerCancellation,
    DeadlineExceeded
}

public enum ProcessTerminalAuthorityKind
{
    Target,
    ContainmentOrExecution,
    CallerCancellation,
    TransitionBudget
}

public enum ProcessPrimaryFailureKind
{
    ContainmentFailure,
    ExecutionFailure,
    CallerCancellation,
    DeadlineExceeded
}

public sealed record ProcessPrimaryFailure
{
    internal ProcessPrimaryFailure(
        ProcessPrimaryFailureKind kind,
        string errorType,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        ErrorType = errorType;
        Message = message;
    }

    public ProcessPrimaryFailureKind Kind { get; }

    public string ErrorType { get; }

    public string Message { get; }
}

public enum ProcessCleanupFailureKind
{
    TerminationFailure,
    ReapFailure,
    TreeQuiescenceFailure,
    StreamDrainFailure,
    CleanupDeadlineExceeded,
    ResourceReleaseFailure
}

public sealed record ProcessCleanupFailure
{
    internal ProcessCleanupFailure(
        ProcessCleanupFailureKind kind,
        string errorType,
        string message)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        ErrorType = errorType;
        Message = message;
    }

    public ProcessCleanupFailureKind Kind { get; }

    public string ErrorType { get; }

    public string Message { get; }
}

public sealed record ProcessTerminalCandidate
{
    internal ProcessTerminalCandidate(
        ProcessTerminalCandidateKind kind,
        long authoritySequence,
        int? exitCode,
        ProcessPrimaryFailure? primaryFailure)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(authoritySequence);
        var isTarget = kind == ProcessTerminalCandidateKind.TargetTerminal;
        if (isTarget != exitCode.HasValue || isTarget == (primaryFailure is not null))
        {
            throw new ArgumentException(
                "Target terminal candidates require an exit code; failure candidates require a primary failure.");
        }

        if (primaryFailure is not null && !Matches(kind, primaryFailure.Kind))
        {
            throw new ArgumentException(
                "Terminal candidate kind must match the primary failure kind.",
                nameof(primaryFailure));
        }

        Kind = kind;
        Authority = GetAuthority(kind);
        AuthoritySequence = authoritySequence;
        ExitCode = exitCode;
        PrimaryFailure = primaryFailure;
    }

    public ProcessTerminalCandidateKind Kind { get; }

    public ProcessTerminalAuthorityKind Authority { get; }

    public long AuthoritySequence { get; }

    public int? ExitCode { get; }

    public ProcessPrimaryFailure? PrimaryFailure { get; }

    private static ProcessTerminalAuthorityKind GetAuthority(
        ProcessTerminalCandidateKind kind)
    {
        return kind switch
        {
            ProcessTerminalCandidateKind.TargetTerminal =>
                ProcessTerminalAuthorityKind.Target,
            ProcessTerminalCandidateKind.ContainmentFailure or
                ProcessTerminalCandidateKind.ExecutionFailure =>
                ProcessTerminalAuthorityKind.ContainmentOrExecution,
            ProcessTerminalCandidateKind.CallerCancellation =>
                ProcessTerminalAuthorityKind.CallerCancellation,
            ProcessTerminalCandidateKind.DeadlineExceeded =>
                ProcessTerminalAuthorityKind.TransitionBudget,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static bool Matches(
        ProcessTerminalCandidateKind candidate,
        ProcessPrimaryFailureKind failure)
    {
        return (candidate, failure) switch
        {
            (ProcessTerminalCandidateKind.ContainmentFailure,
                ProcessPrimaryFailureKind.ContainmentFailure) => true,
            (ProcessTerminalCandidateKind.ExecutionFailure,
                ProcessPrimaryFailureKind.ExecutionFailure) => true,
            (ProcessTerminalCandidateKind.CallerCancellation,
                ProcessPrimaryFailureKind.CallerCancellation) => true,
            (ProcessTerminalCandidateKind.DeadlineExceeded,
                ProcessPrimaryFailureKind.DeadlineExceeded) => true,
            _ => false
        };
    }
}

public sealed class ProcessSupervisionOutcome
{
    internal ProcessSupervisionOutcome(
        ProcessTerminalCandidate terminal,
        ProcessSupervisionProof proof,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        Terminal = terminal;
        Proof = proof;
        CleanupFailures = new ReadOnlyCollection<ProcessCleanupFailure>(
            cleanupFailures.ToArray());
    }

    public ProcessTerminalCandidate Terminal { get; }

    public ProcessPrimaryFailure? PrimaryFailure => Terminal.PrimaryFailure;

    public ProcessSupervisionProof Proof { get; }

    public IReadOnlyList<ProcessCleanupFailure> CleanupFailures { get; }
}

internal static class ProcessTerminalCandidateFactory
{
    internal static ProcessTerminalCandidate TargetTerminal(
        long authoritySequence,
        int exitCode)
    {
        return new ProcessTerminalCandidate(
            ProcessTerminalCandidateKind.TargetTerminal,
            authoritySequence,
            exitCode,
            null);
    }

    internal static ProcessTerminalCandidate PrimaryFailure(
        ProcessPrimaryFailureKind kind,
        long authoritySequence,
        string errorType,
        string message)
    {
        var candidateKind = kind switch
        {
            ProcessPrimaryFailureKind.ContainmentFailure =>
                ProcessTerminalCandidateKind.ContainmentFailure,
            ProcessPrimaryFailureKind.ExecutionFailure =>
                ProcessTerminalCandidateKind.ExecutionFailure,
            ProcessPrimaryFailureKind.CallerCancellation =>
                ProcessTerminalCandidateKind.CallerCancellation,
            ProcessPrimaryFailureKind.DeadlineExceeded =>
                ProcessTerminalCandidateKind.DeadlineExceeded,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return new ProcessTerminalCandidate(
            candidateKind,
            authoritySequence,
            null,
            new ProcessPrimaryFailure(kind, errorType, message));
    }
}

internal static class ProcessTerminalSelectionPolicy
{
    internal static ProcessTerminalCandidate Select(
        IEnumerable<ProcessTerminalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var snapshot = candidates.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one terminal candidate is required.",
                nameof(candidates));
        }

        var consistentCandidates = snapshot
            .GroupBy(candidate => (
                candidate.Authority,
                candidate.AuthoritySequence))
            .Select(RequireConsistentPublication)
            .ToArray();
        var authoritativeCandidates = consistentCandidates
            .GroupBy(candidate => candidate.Authority)
            .Select(SelectFirstFromAuthority)
            .OrderBy(candidate => Precedence(candidate.Authority))
            .ToArray();
        return authoritativeCandidates[0];
    }

    private static ProcessTerminalCandidate RequireConsistentPublication(
        IGrouping<
            (ProcessTerminalAuthorityKind Authority, long AuthoritySequence),
            ProcessTerminalCandidate> publications)
    {
        var distinct = publications.Distinct().ToArray();
        if (distinct.Length != 1)
        {
            throw new InvalidOperationException(
                $"Authority {publications.Key.Authority} published contradictory candidates " +
                $"at sequence {publications.Key.AuthoritySequence}.");
        }

        return distinct[0];
    }

    private static ProcessTerminalCandidate SelectFirstFromAuthority(
        IGrouping<ProcessTerminalAuthorityKind, ProcessTerminalCandidate> candidates)
    {
        return candidates.OrderBy(candidate => candidate.AuthoritySequence).First();
    }

    private static int Precedence(ProcessTerminalAuthorityKind authority)
    {
        return authority switch
        {
            ProcessTerminalAuthorityKind.Target => 0,
            ProcessTerminalAuthorityKind.ContainmentOrExecution => 1,
            ProcessTerminalAuthorityKind.CallerCancellation => 2,
            ProcessTerminalAuthorityKind.TransitionBudget => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(authority), authority, null)
        };
    }
}
