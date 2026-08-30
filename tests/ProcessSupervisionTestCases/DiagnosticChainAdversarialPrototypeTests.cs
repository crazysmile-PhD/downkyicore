namespace DownKyi.ProcessSupervision.Tests;

public sealed class DiagnosticChainAdversarialPrototypeTests
{
    private static readonly DiagnosticToyTransition[] ExpectedTransitions =
    [
        DiagnosticToyTransition.RequestCreated,
        DiagnosticToyTransition.CollectorDispatchRequested,
        DiagnosticToyTransition.ProcessStartRequested,
        DiagnosticToyTransition.ProcessStartReturned,
        DiagnosticToyTransition.ContainmentPrepared,
        DiagnosticToyTransition.ContainmentEstablished,
        DiagnosticToyTransition.ControlChannelConnected,
        DiagnosticToyTransition.StatusChannelConnected,
        DiagnosticToyTransition.OwnershipAcknowledged,
        DiagnosticToyTransition.TargetLaunchAuthorized,
        DiagnosticToyTransition.TargetStarted,
        DiagnosticToyTransition.FirstObservableProgress,
        DiagnosticToyTransition.EvidenceCaptured,
        DiagnosticToyTransition.EvidencePersisted,
        DiagnosticToyTransition.TargetExited,
        DiagnosticToyTransition.OperationDeadlineExhausted,
        DiagnosticToyTransition.OwnerObservedDeadlineExhaustion,
        DiagnosticToyTransition.TerminationStarted,
        DiagnosticToyTransition.TerminationCompleted,
        DiagnosticToyTransition.ReapCompleted,
        DiagnosticToyTransition.StreamsDrained,
        DiagnosticToyTransition.DiagnosticOutcomeReturned
    ];

    [Fact]
    public void TransitionModelRetainsEveryMaterialDiagnosticBoundary()
    {
        Assert.Equal(ExpectedTransitions, Enum.GetValues<DiagnosticToyTransition>());
    }

    [Fact]
    public void HealthyChainCompletesThroughPrimaryStructuredEvidence()
    {
        var run = Run(DiagnosticToyFault.None);

        Assert.Equal(DiagnosticToyFailureKind.None, run.Diagnosis.PrimaryFailure);
        Assert.Equal(DiagnosticToyFailureDomain.None, run.Diagnosis.FailureDomain);
        Assert.False(run.Diagnosis.UsedOwnerJournalFallback);
        Assert.True(run.PrimaryEvidence.StackPayloadCaptured);
        Assert.True(run.PrimaryEvidence.StructuredEvidenceAvailable);
        Assert.True(run.PrimaryEvidence.PersistedArtifactAvailable);
        Assert.True(run.Diagnosis.TargetStarted);
        Assert.True(run.Diagnosis.TargetExited);
        Assert.True(run.Diagnosis.ReapCompleted);
        Assert.True(run.Diagnosis.StreamsDrained);
        Assert.True(run.Diagnosis.EvidenceCaptured);
        Assert.True(run.Diagnosis.EvidencePersisted);
        Assert.Null(run.Diagnosis.FirstMissingRequired);
        Assert.Empty(run.Diagnosis.SecondaryFailures);
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    [Theory]
    [InlineData(1, 1, 1, 0, 1, false, false, -1)]
    [InlineData(2, 2, 2, 2, 3, true, false, -1)]
    [InlineData(4, 3, 3, 3, 4, false, false, -1)]
    [InlineData(8, 4, 4, 4, 5, false, false, -1)]
    [InlineData(16, 5, 5, 5, 6, true, false, -1)]
    [InlineData(32, 6, 6, 6, 7, true, false, -1)]
    [InlineData(64, 7, 7, 7, 8, true, false, -1)]
    [InlineData(128, 8, 8, 9, 10, true, false, -1)]
    [InlineData(256, 9, 9, 10, 11, true, true, -1)]
    [InlineData(512, 10, 10, 12, 13, false, true, -1)]
    [InlineData(1024, 12, 10, 12, 13, false, true, -1)]
    [InlineData(4096, 11, 11, 13, 14, false, true, 13)]
    [InlineData(8192, 11, 11, 13, 14, false, true, 14)]
    [InlineData(16384, 11, 11, 13, 14, false, true, 15)]
    public void EverySingleNodeAttackRetainsItsCausalInterval(
        int faultValue,
        int failureValue,
        int domainValue,
        int lastKnownGoodValue,
        int firstMissingValue,
        bool deadlineExhausted,
        bool targetStarted,
        int secondaryFailureValue)
    {
        var fault = (DiagnosticToyFault)faultValue;
        var expectedFailure = (DiagnosticToyFailureKind)failureValue;
        var expectedDomain = (DiagnosticToyFailureDomain)domainValue;
        var expectedLastKnownGood = (DiagnosticToyTransition)lastKnownGoodValue;
        var expectedFirstMissing = (DiagnosticToyTransition)firstMissingValue;
        var run = Run(fault);

        Assert.Equal(expectedFailure, run.Diagnosis.PrimaryFailure);
        Assert.Equal(expectedDomain, run.Diagnosis.FailureDomain);
        Assert.Equal(expectedLastKnownGood, run.Diagnosis.LastKnownGood);
        Assert.Equal(expectedFirstMissing, run.Diagnosis.FirstMissingRequired);
        Assert.Equal(deadlineExhausted, run.Diagnosis.DeadlineExhausted);
        Assert.Equal(targetStarted, run.Diagnosis.TargetStarted);
        Assert.Equal(
            fault is not DiagnosticToyFault.DispatchFailure and not
                DiagnosticToyFault.ProcessStartStall
                ? 4101L
                : null,
            run.Diagnosis.SupervisorProcessId);
        Assert.Equal(targetStarted ? 4201L : null, run.Diagnosis.TargetProcessId);
        if (secondaryFailureValue < 0)
        {
            Assert.Empty(run.Diagnosis.SecondaryFailures);
        }
        else
        {
            Assert.Contains(
                (DiagnosticToyFailureKind)secondaryFailureValue,
                run.Diagnosis.SecondaryFailures);
        }
        Assert.NotEqual(
            DiagnosticToyFailureKind.AmbiguousMissingEvidence,
            run.Diagnosis.PrimaryFailure);
        Assert.True(run.Diagnosis.TerminationStarted ==
            run.OwnerJournal.Contains(DiagnosticToyTransition.ProcessStartReturned));
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    [Fact]
    public void CaptureFailureAndPersistenceFailureRemainDistinct()
    {
        var captureFailure = Run(DiagnosticToyFault.CaptureNoProgress);
        var persistenceFailure = Run(DiagnosticToyFault.EvidencePersistenceFailure);

        Assert.Equal(
            DiagnosticToyFailureKind.EvidenceCaptureFailure,
            captureFailure.Diagnosis.PrimaryFailure);
        Assert.Equal(
            DiagnosticToyFailureDomain.EvidenceCapture,
            captureFailure.Diagnosis.FailureDomain);
        Assert.False(captureFailure.Diagnosis.EvidenceCaptured);
        Assert.False(captureFailure.Diagnosis.EvidencePersisted);

        Assert.Equal(
            DiagnosticToyFailureKind.EvidencePersistenceFailure,
            persistenceFailure.Diagnosis.PrimaryFailure);
        Assert.Equal(
            DiagnosticToyFailureDomain.EvidencePersistence,
            persistenceFailure.Diagnosis.FailureDomain);
        Assert.True(persistenceFailure.PrimaryEvidence.StackPayloadCaptured);
        Assert.True(persistenceFailure.Diagnosis.EvidenceCaptured);
        Assert.False(persistenceFailure.Diagnosis.EvidencePersisted);
        AssertJournalIsMonotonicAndUtcFree(captureFailure);
        AssertJournalIsMonotonicAndUtcFree(persistenceFailure);
    }

    [Fact]
    public void DiagnosticBlackoutFallsBackToTheBoundedOwnerJournal()
    {
        var run = Run(
            DiagnosticToyFault.ControlChannelFailure |
            DiagnosticToyFault.CorruptPrimaryEvidence);

        Assert.False(run.PrimaryEvidence.StackPayloadCaptured);
        Assert.False(run.PrimaryEvidence.StructuredEvidenceAvailable);
        Assert.False(run.PrimaryEvidence.PersistedArtifactAvailable);
        Assert.True(run.Diagnosis.UsedOwnerJournalFallback);
        Assert.Equal(
            DiagnosticToyFailureKind.ControlChannelFailure,
            run.Diagnosis.PrimaryFailure);
        Assert.Equal(
            DiagnosticToyFailureDomain.ControlChannelStartup,
            run.Diagnosis.FailureDomain);
        Assert.Equal(
            DiagnosticToyTransition.ContainmentEstablished,
            run.Diagnosis.LastKnownGood);
        Assert.Equal(
            DiagnosticToyTransition.ControlChannelConnected,
            run.Diagnosis.FirstMissingRequired);
        Assert.True(run.Diagnosis.DeadlineExhausted);
        Assert.Equal(4101L, run.Diagnosis.SupervisorProcessId);
        Assert.Null(run.Diagnosis.TargetProcessId);
        Assert.False(run.Diagnosis.TargetStarted);
        Assert.False(run.Diagnosis.TargetExited);
        Assert.True(run.Diagnosis.TerminationStarted);
        Assert.True(run.Diagnosis.ReapCompleted);
        Assert.Contains(
            DiagnosticToyFailureKind.EvidenceSystemFailure,
            run.Diagnosis.SecondaryFailures);
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    [Theory]
    [InlineData(1026, 2, 12, true, false, false)]
    [InlineData(10240, 11, 14, false, true, true)]
    [InlineData(16896, 10, 15, false, true, true)]
    [InlineData(4160, 7, 13, true, true, false)]
    public void PairwiseFaultsKeepPrimaryCauseAndSecondaryFailureSeparate(
        int faultValue,
        int primaryFailureValue,
        int secondaryFailureValue,
        bool deadlineExhausted,
        bool terminationStarted,
        bool terminationCompleted)
    {
        var run = Run((DiagnosticToyFault)faultValue);

        Assert.Equal(
            (DiagnosticToyFailureKind)primaryFailureValue,
            run.Diagnosis.PrimaryFailure);
        Assert.Contains(
            (DiagnosticToyFailureKind)secondaryFailureValue,
            run.Diagnosis.SecondaryFailures);
        Assert.Equal(deadlineExhausted, run.Diagnosis.DeadlineExhausted);
        Assert.Equal(terminationStarted, run.Diagnosis.TerminationStarted);
        Assert.Equal(terminationCompleted, run.Diagnosis.TerminationCompleted);
        Assert.NotEqual(
            DiagnosticToyFailureKind.AmbiguousMissingEvidence,
            run.Diagnosis.PrimaryFailure);
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    [Fact]
    public void OwnerJournalIsBoundedAndContainsNoPayloadFields()
    {
        var run = Run(
            DiagnosticToyFault.ControlChannelFailure |
            DiagnosticToyFault.CorruptPrimaryEvidence);

        Assert.InRange(run.OwnerJournal.Transitions.Count, 1, 22);
        Assert.InRange(run.OwnerJournal.SecondaryFailures.Count, 0, 4);
        Assert.DoesNotContain(
            typeof(DiagnosticToyTransitionEntry).GetProperties(),
            property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(
            typeof(DiagnosticToyOwnerJournal).GetProperties(),
            property => property.PropertyType == typeof(string));
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    [Theory]
    [InlineData(2, 2, 2, 3)]
    [InlineData(16, 5, 5, 6)]
    [InlineData(128, 8, 9, 10)]
    [InlineData(512, 10, 12, 13)]
    public void TransitionIntervalDeterminesFailureDomainWithoutElapsedThresholds(
        int faultValue,
        int domainValue,
        int lastKnownGoodValue,
        int firstMissingValue)
    {
        var run = Run((DiagnosticToyFault)faultValue);

        Assert.Equal(
            (DiagnosticToyFailureDomain)domainValue,
            run.Diagnosis.FailureDomain);
        Assert.Equal(
            (DiagnosticToyTransition)lastKnownGoodValue,
            run.Diagnosis.LastKnownGood);
        Assert.Equal(
            (DiagnosticToyTransition)firstMissingValue,
            run.Diagnosis.FirstMissingRequired);
        AssertJournalIsMonotonicAndUtcFree(run);
    }

    private static DiagnosticToyRun Run(DiagnosticToyFault fault)
    {
        return new DiagnosticChainAdversarialPrototype().Run(fault);
    }

    private static void AssertJournalIsMonotonicAndUtcFree(DiagnosticToyRun run)
    {
        Assert.Equal(0, run.UtcReadCount);
        Assert.Equal(
            run.OwnerJournal.Transitions.Count,
            run.OwnerJournal.Transitions
                .Select(entry => entry.Transition)
                .Distinct()
                .Count());
        Assert.All(
            run.OwnerJournal.Transitions.Zip(
                run.OwnerJournal.Transitions.Skip(1)),
            pair => Assert.True(pair.First.Elapsed <= pair.Second.Elapsed));
    }
}
