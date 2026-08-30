using System.Collections.ObjectModel;
using DownKyi.ProcessSupervision;

namespace DownKyi.ProcessSupervision.Tests;

internal enum DiagnosticToyTransition
{
    RequestCreated,
    CollectorDispatchRequested,
    ProcessStartRequested,
    ProcessStartReturned,
    ContainmentPrepared,
    ContainmentEstablished,
    ControlChannelConnected,
    StatusChannelConnected,
    OwnershipAcknowledged,
    TargetLaunchAuthorized,
    TargetStarted,
    FirstObservableProgress,
    EvidenceCaptured,
    EvidencePersisted,
    TargetExited,
    OperationDeadlineExhausted,
    OwnerObservedDeadlineExhaustion,
    TerminationStarted,
    TerminationCompleted,
    ReapCompleted,
    StreamsDrained,
    DiagnosticOutcomeReturned
}

[Flags]
internal enum DiagnosticToyFault
{
    None = 0,
    DispatchFailure = 1 << 0,
    ProcessStartStall = 1 << 1,
    ContainmentPreparationFailure = 1 << 2,
    ContainmentEstablishmentFailure = 1 << 3,
    ControlChannelFailure = 1 << 4,
    StatusChannelFailure = 1 << 5,
    OwnershipAcknowledgementFailure = 1 << 6,
    TargetLaunchAcknowledgementFailure = 1 << 7,
    CaptureNoProgress = 1 << 8,
    EvidencePersistenceFailure = 1 << 9,
    CorruptPrimaryEvidence = 1 << 10,
    PrimaryOperationFailure = 1 << 11,
    TerminationFailure = 1 << 12,
    ReapFailure = 1 << 13,
    StreamDrainFailure = 1 << 14
}

internal enum DiagnosticToyFailureKind
{
    None,
    CollectorDispatchFailure,
    ProcessStartBoundaryFailure,
    ContainmentPreparationFailure,
    ContainmentEstablishmentFailure,
    ControlChannelFailure,
    StatusChannelFailure,
    OwnershipAcknowledgementFailure,
    TargetLaunchFailure,
    EvidenceCaptureFailure,
    EvidencePersistenceFailure,
    PrimaryOperationFailure,
    EvidenceSystemFailure,
    TerminationFailure,
    ReapFailure,
    StreamDrainFailure,
    AmbiguousMissingEvidence
}

internal enum DiagnosticToyFailureDomain
{
    None,
    CollectorDispatch,
    ProcessStart,
    ContainmentPreparation,
    ContainmentEstablishment,
    ControlChannelStartup,
    StatusChannelStartup,
    OwnershipHandshake,
    TargetLaunch,
    EvidenceCapture,
    EvidencePersistence,
    TargetCompletion,
    Cleanup,
    OutcomeAggregation
}

internal sealed record DiagnosticToyTransitionEntry(
    DiagnosticToyTransition Transition,
    TimeSpan Elapsed,
    long? SupervisorProcessId,
    long? TargetProcessId);

internal sealed record DiagnosticToyPrimaryFailure(
    DiagnosticToyFailureKind Kind,
    DiagnosticToyTransition LastKnownGood,
    DiagnosticToyTransition FirstMissingRequired);

internal sealed record DiagnosticToyPrimaryEvidence(
    bool StackPayloadCaptured,
    bool StructuredEvidenceAvailable,
    bool PersistedArtifactAvailable,
    DiagnosticToyPrimaryFailure? Failure);

internal sealed record DiagnosticToyDiagnosis(
    DiagnosticToyFailureKind PrimaryFailure,
    DiagnosticToyFailureDomain FailureDomain,
    DiagnosticToyTransition LastKnownGood,
    DiagnosticToyTransition? FirstMissingRequired,
    bool UsedOwnerJournalFallback,
    bool DeadlineExhausted,
    bool TargetStarted,
    bool TargetExited,
    bool TerminationStarted,
    bool TerminationCompleted,
    bool ReapCompleted,
    bool StreamsDrained,
    bool EvidenceCaptured,
    bool EvidencePersisted,
    long? SupervisorProcessId,
    long? TargetProcessId,
    IReadOnlyList<DiagnosticToyFailureKind> SecondaryFailures);

internal sealed record DiagnosticToyRun(
    DiagnosticToyPrimaryEvidence PrimaryEvidence,
    DiagnosticToyOwnerJournal OwnerJournal,
    DiagnosticToyDiagnosis Diagnosis,
    int UtcReadCount);

internal sealed class DiagnosticToyOwnerJournal
{
    private const int MaximumTransitionEntries = 22;
    private const int MaximumSecondaryFailures = 4;
    private readonly TransitionBudget _budget;
    private readonly List<DiagnosticToyTransitionEntry> _transitions = [];
    private readonly List<DiagnosticToyFailureKind> _secondaryFailures = [];
    private DiagnosticToyPrimaryFailure? _primaryFailure;
    private long? _supervisorProcessId;
    private long? _targetProcessId;

    public DiagnosticToyOwnerJournal(TransitionBudget budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public IReadOnlyList<DiagnosticToyTransitionEntry> Transitions =>
        new ReadOnlyCollection<DiagnosticToyTransitionEntry>(_transitions);

    public IReadOnlyList<DiagnosticToyFailureKind> SecondaryFailures =>
        new ReadOnlyCollection<DiagnosticToyFailureKind>(_secondaryFailures);

    public DiagnosticToyPrimaryFailure? PrimaryFailure => _primaryFailure;

    public long? SupervisorProcessId => _supervisorProcessId;

    public long? TargetProcessId => _targetProcessId;

    public void SetSupervisorIdentity(long processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _supervisorProcessId ??= processId;
    }

    public void SetTargetIdentity(long processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _targetProcessId ??= processId;
    }

    public void Mark(DiagnosticToyTransition transition)
    {
        if (_transitions.Count >= MaximumTransitionEntries)
        {
            throw new InvalidOperationException("The bounded owner journal is full.");
        }
        if (_transitions.Any(entry => entry.Transition == transition))
        {
            throw new InvalidOperationException($"The transition was already recorded: {transition}");
        }

        var elapsed = _budget.Elapsed;
        if (_transitions.Count > 0 && elapsed < _transitions[^1].Elapsed)
        {
            throw new InvalidOperationException("The owner journal must remain monotonic.");
        }

        _transitions.Add(new DiagnosticToyTransitionEntry(
            transition,
            elapsed,
            _supervisorProcessId,
            _targetProcessId));
    }

    public bool Contains(DiagnosticToyTransition transition)
    {
        return _transitions.Any(entry => entry.Transition == transition);
    }

    public void RecordPrimaryFailure(
        DiagnosticToyFailureKind kind,
        DiagnosticToyTransition firstMissingRequired)
    {
        if (_primaryFailure != null)
        {
            throw new InvalidOperationException("The primary causal failure is immutable.");
        }

        var lastKnownGood = _transitions
            .Where(entry => IsCausalTransition(entry.Transition))
            .Select(entry => entry.Transition)
            .Last();
        _primaryFailure = new DiagnosticToyPrimaryFailure(
            kind,
            lastKnownGood,
            firstMissingRequired);
    }

    public void RecordSecondaryFailure(DiagnosticToyFailureKind kind)
    {
        if (_secondaryFailures.Count >= MaximumSecondaryFailures)
        {
            throw new InvalidOperationException("The bounded secondary-failure journal is full.");
        }

        _secondaryFailures.Add(kind);
    }

    private static bool IsCausalTransition(DiagnosticToyTransition transition)
    {
        return transition is not
            DiagnosticToyTransition.OperationDeadlineExhausted and not
            DiagnosticToyTransition.OwnerObservedDeadlineExhaustion and not
            DiagnosticToyTransition.TerminationStarted and not
            DiagnosticToyTransition.TerminationCompleted and not
            DiagnosticToyTransition.ReapCompleted and not
            DiagnosticToyTransition.StreamsDrained and not
            DiagnosticToyTransition.DiagnosticOutcomeReturned;
    }
}

internal static class DiagnosticToyAggregator
{
    public static DiagnosticToyDiagnosis Diagnose(
        DiagnosticToyPrimaryEvidence primaryEvidence,
        DiagnosticToyOwnerJournal ownerJournal)
    {
        ArgumentNullException.ThrowIfNull(primaryEvidence);
        ArgumentNullException.ThrowIfNull(ownerJournal);
        var useFallback = !primaryEvidence.StructuredEvidenceAvailable;
        var primaryFailure = useFallback
            ? ownerJournal.PrimaryFailure
            : primaryEvidence.Failure ?? ownerJournal.PrimaryFailure;

        if (primaryFailure == null)
        {
            return new DiagnosticToyDiagnosis(
                DiagnosticToyFailureKind.None,
                DiagnosticToyFailureDomain.None,
                DiagnosticToyTransition.DiagnosticOutcomeReturned,
                FirstMissingRequired: null,
                useFallback,
                DeadlineExhausted: false,
                TargetStarted: ownerJournal.Contains(DiagnosticToyTransition.TargetStarted),
                TargetExited: ownerJournal.Contains(DiagnosticToyTransition.TargetExited),
                TerminationStarted: false,
                TerminationCompleted: false,
                ReapCompleted: ownerJournal.Contains(DiagnosticToyTransition.ReapCompleted),
                StreamsDrained: ownerJournal.Contains(DiagnosticToyTransition.StreamsDrained),
                EvidenceCaptured: ownerJournal.Contains(DiagnosticToyTransition.EvidenceCaptured),
                EvidencePersisted: ownerJournal.Contains(DiagnosticToyTransition.EvidencePersisted),
                ownerJournal.SupervisorProcessId,
                ownerJournal.TargetProcessId,
                ownerJournal.SecondaryFailures);
        }

        return new DiagnosticToyDiagnosis(
            primaryFailure.Kind,
            Classify(primaryFailure.FirstMissingRequired),
            primaryFailure.LastKnownGood,
            primaryFailure.FirstMissingRequired,
            useFallback,
            ownerJournal.Contains(DiagnosticToyTransition.OperationDeadlineExhausted),
            ownerJournal.Contains(DiagnosticToyTransition.TargetStarted),
            ownerJournal.Contains(DiagnosticToyTransition.TargetExited),
            ownerJournal.Contains(DiagnosticToyTransition.TerminationStarted),
            ownerJournal.Contains(DiagnosticToyTransition.TerminationCompleted),
            ownerJournal.Contains(DiagnosticToyTransition.ReapCompleted),
            ownerJournal.Contains(DiagnosticToyTransition.StreamsDrained),
            ownerJournal.Contains(DiagnosticToyTransition.EvidenceCaptured),
            ownerJournal.Contains(DiagnosticToyTransition.EvidencePersisted),
            ownerJournal.SupervisorProcessId,
            ownerJournal.TargetProcessId,
            ownerJournal.SecondaryFailures);
    }

    private static DiagnosticToyFailureDomain Classify(
        DiagnosticToyTransition firstMissingRequired)
    {
        return firstMissingRequired switch
        {
            DiagnosticToyTransition.CollectorDispatchRequested =>
                DiagnosticToyFailureDomain.CollectorDispatch,
            DiagnosticToyTransition.ProcessStartRequested or
            DiagnosticToyTransition.ProcessStartReturned =>
                DiagnosticToyFailureDomain.ProcessStart,
            DiagnosticToyTransition.ContainmentPrepared =>
                DiagnosticToyFailureDomain.ContainmentPreparation,
            DiagnosticToyTransition.ContainmentEstablished =>
                DiagnosticToyFailureDomain.ContainmentEstablishment,
            DiagnosticToyTransition.ControlChannelConnected =>
                DiagnosticToyFailureDomain.ControlChannelStartup,
            DiagnosticToyTransition.StatusChannelConnected =>
                DiagnosticToyFailureDomain.StatusChannelStartup,
            DiagnosticToyTransition.OwnershipAcknowledged =>
                DiagnosticToyFailureDomain.OwnershipHandshake,
            DiagnosticToyTransition.TargetLaunchAuthorized or
            DiagnosticToyTransition.TargetStarted =>
                DiagnosticToyFailureDomain.TargetLaunch,
            DiagnosticToyTransition.FirstObservableProgress or
            DiagnosticToyTransition.EvidenceCaptured =>
                DiagnosticToyFailureDomain.EvidenceCapture,
            DiagnosticToyTransition.EvidencePersisted =>
                DiagnosticToyFailureDomain.EvidencePersistence,
            DiagnosticToyTransition.TargetExited =>
                DiagnosticToyFailureDomain.TargetCompletion,
            DiagnosticToyTransition.ReapCompleted or
            DiagnosticToyTransition.StreamsDrained =>
                DiagnosticToyFailureDomain.Cleanup,
            DiagnosticToyTransition.DiagnosticOutcomeReturned =>
                DiagnosticToyFailureDomain.OutcomeAggregation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(firstMissingRequired),
                firstMissingRequired,
                null)
        };
    }
}

internal sealed class DiagnosticChainAdversarialPrototype
{
    private const long SupervisorProcessId = 4101;
    private const long TargetProcessId = 4201;
    private static readonly TimeSpan OperationDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromMilliseconds(50);
    private readonly DiagnosticToyTimeProvider _timeProvider = new();
    private readonly TransitionBudget _budget;
    private readonly DiagnosticToyOwnerJournal _journal;
    private DiagnosticToyPrimaryEvidence _primaryEvidence = new(
        StackPayloadCaptured: false,
        StructuredEvidenceAvailable: true,
        PersistedArtifactAvailable: false,
        Failure: null);

    public DiagnosticChainAdversarialPrototype()
    {
        _budget = TransitionBudget.Start(
            OperationDuration,
            CleanupGrace,
            _timeProvider);
        _journal = new DiagnosticToyOwnerJournal(_budget);
    }

    public DiagnosticToyRun Run(DiagnosticToyFault faults)
    {
        Mark(DiagnosticToyTransition.RequestCreated);
        if (FailIf(
                faults,
                DiagnosticToyFault.DispatchFailure,
                DiagnosticToyFailureKind.CollectorDispatchFailure,
                DiagnosticToyTransition.CollectorDispatchRequested))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.CollectorDispatchRequested);
        Mark(DiagnosticToyTransition.ProcessStartRequested);
        if (FailIf(
                faults,
                DiagnosticToyFault.ProcessStartStall,
                DiagnosticToyFailureKind.ProcessStartBoundaryFailure,
                DiagnosticToyTransition.ProcessStartReturned,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        _journal.SetSupervisorIdentity(SupervisorProcessId);
        Mark(DiagnosticToyTransition.ProcessStartReturned);
        if (FailIf(
                faults,
                DiagnosticToyFault.ContainmentPreparationFailure,
                DiagnosticToyFailureKind.ContainmentPreparationFailure,
                DiagnosticToyTransition.ContainmentPrepared))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.ContainmentPrepared);
        if (FailIf(
                faults,
                DiagnosticToyFault.ContainmentEstablishmentFailure,
                DiagnosticToyFailureKind.ContainmentEstablishmentFailure,
                DiagnosticToyTransition.ContainmentEstablished))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.ContainmentEstablished);
        if (FailIf(
                faults,
                DiagnosticToyFault.ControlChannelFailure,
                DiagnosticToyFailureKind.ControlChannelFailure,
                DiagnosticToyTransition.ControlChannelConnected,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.ControlChannelConnected);
        if (FailIf(
                faults,
                DiagnosticToyFault.StatusChannelFailure,
                DiagnosticToyFailureKind.StatusChannelFailure,
                DiagnosticToyTransition.StatusChannelConnected,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.StatusChannelConnected);
        if (FailIf(
                faults,
                DiagnosticToyFault.OwnershipAcknowledgementFailure,
                DiagnosticToyFailureKind.OwnershipAcknowledgementFailure,
                DiagnosticToyTransition.OwnershipAcknowledged,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.OwnershipAcknowledged);
        Mark(DiagnosticToyTransition.TargetLaunchAuthorized);
        if (FailIf(
                faults,
                DiagnosticToyFault.TargetLaunchAcknowledgementFailure,
                DiagnosticToyFailureKind.TargetLaunchFailure,
                DiagnosticToyTransition.TargetStarted,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        _journal.SetTargetIdentity(TargetProcessId);
        Mark(DiagnosticToyTransition.TargetStarted);
        if (FailIf(
                faults,
                DiagnosticToyFault.CaptureNoProgress,
                DiagnosticToyFailureKind.EvidenceCaptureFailure,
                DiagnosticToyTransition.FirstObservableProgress,
                exhaustDeadline: true))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.FirstObservableProgress);
        Mark(DiagnosticToyTransition.EvidenceCaptured);
        _primaryEvidence = _primaryEvidence with
        {
            StackPayloadCaptured = true
        };
        if (FailIf(
                faults,
                DiagnosticToyFault.EvidencePersistenceFailure,
                DiagnosticToyFailureKind.EvidencePersistenceFailure,
                DiagnosticToyTransition.EvidencePersisted))
        {
            return Finish(faults);
        }
        if (FailIf(
                faults,
                DiagnosticToyFault.CorruptPrimaryEvidence,
                DiagnosticToyFailureKind.EvidenceSystemFailure,
                DiagnosticToyTransition.EvidencePersisted))
        {
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.EvidencePersisted);
        _primaryEvidence = _primaryEvidence with
        {
            PersistedArtifactAvailable = true
        };
        var cleanupFaults = DiagnosticToyFault.TerminationFailure |
            DiagnosticToyFault.ReapFailure |
            DiagnosticToyFault.StreamDrainFailure;
        if (faults.HasFlag(DiagnosticToyFault.PrimaryOperationFailure) ||
            (faults & cleanupFaults) != DiagnosticToyFault.None)
        {
            RecordPrimaryFailure(
                DiagnosticToyFailureKind.PrimaryOperationFailure,
                DiagnosticToyTransition.TargetExited);
            return Finish(faults);
        }

        Mark(DiagnosticToyTransition.TargetExited);
        Mark(DiagnosticToyTransition.ReapCompleted);
        Mark(DiagnosticToyTransition.StreamsDrained);
        return Finish(faults);
    }

    private bool FailIf(
        DiagnosticToyFault faults,
        DiagnosticToyFault expected,
        DiagnosticToyFailureKind kind,
        DiagnosticToyTransition firstMissingRequired,
        bool exhaustDeadline = false)
    {
        if (!faults.HasFlag(expected))
        {
            return false;
        }

        RecordPrimaryFailure(kind, firstMissingRequired);
        if (exhaustDeadline)
        {
            ExhaustOperationDeadline();
        }
        return true;
    }

    private void RecordPrimaryFailure(
        DiagnosticToyFailureKind kind,
        DiagnosticToyTransition firstMissingRequired)
    {
        _journal.RecordPrimaryFailure(kind, firstMissingRequired);
        _primaryEvidence = _primaryEvidence with
        {
            Failure = _journal.PrimaryFailure
        };
    }

    private void ExhaustOperationDeadline()
    {
        _timeProvider.Advance(_budget.RemainingOperation);
        _journal.Mark(DiagnosticToyTransition.OperationDeadlineExhausted);
        _journal.Mark(DiagnosticToyTransition.OwnerObservedDeadlineExhaustion);
    }

    private DiagnosticToyRun Finish(DiagnosticToyFault faults)
    {
        if (_journal.PrimaryFailure != null &&
            _journal.Contains(DiagnosticToyTransition.ProcessStartReturned))
        {
            RunCleanup(faults);
        }

        if (faults.HasFlag(DiagnosticToyFault.CorruptPrimaryEvidence))
        {
            _primaryEvidence = _primaryEvidence with
            {
                StructuredEvidenceAvailable = false,
                PersistedArtifactAvailable = false,
                Failure = null
            };
            if (_journal.PrimaryFailure?.Kind != DiagnosticToyFailureKind.EvidenceSystemFailure)
            {
                _journal.RecordSecondaryFailure(DiagnosticToyFailureKind.EvidenceSystemFailure);
            }
        }

        Mark(DiagnosticToyTransition.DiagnosticOutcomeReturned);
        var diagnosis = DiagnosticToyAggregator.Diagnose(_primaryEvidence, _journal);
        return new DiagnosticToyRun(
            _primaryEvidence,
            _journal,
            diagnosis,
            _timeProvider.UtcReadCount);
    }

    private void RunCleanup(DiagnosticToyFault faults)
    {
        Mark(DiagnosticToyTransition.TerminationStarted);
        if (faults.HasFlag(DiagnosticToyFault.TerminationFailure))
        {
            _journal.RecordSecondaryFailure(DiagnosticToyFailureKind.TerminationFailure);
        }
        else
        {
            Mark(DiagnosticToyTransition.TerminationCompleted);
            if (_journal.Contains(DiagnosticToyTransition.TargetStarted))
            {
                Mark(DiagnosticToyTransition.TargetExited);
            }
        }

        if (faults.HasFlag(DiagnosticToyFault.ReapFailure))
        {
            _journal.RecordSecondaryFailure(DiagnosticToyFailureKind.ReapFailure);
        }
        else
        {
            Mark(DiagnosticToyTransition.ReapCompleted);
        }

        if (faults.HasFlag(DiagnosticToyFault.StreamDrainFailure))
        {
            _journal.RecordSecondaryFailure(DiagnosticToyFailureKind.StreamDrainFailure);
        }
        else
        {
            Mark(DiagnosticToyTransition.StreamsDrained);
        }
    }

    private void Mark(DiagnosticToyTransition transition)
    {
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        _journal.Mark(transition);
    }
}

internal sealed class DiagnosticToyTimeProvider : TimeProvider
{
    private long _timestamp;

    public int UtcReadCount { get; private set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        return _timestamp;
    }

    public override DateTimeOffset GetUtcNow()
    {
        UtcReadCount++;
        return DateTimeOffset.UnixEpoch;
    }

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        _timestamp = checked(_timestamp + duration.Ticks);
    }
}
