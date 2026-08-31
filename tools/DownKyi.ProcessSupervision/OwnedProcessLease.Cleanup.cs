using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // Public process-supervision boundary is consumed by PowerShell and platform tests.
public sealed partial class OwnedProcessLease
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Initialization cleanup must retain every typed failure before the lease can be rejected.")]
    private async Task<OwnedProcessOutcome> CompleteInitializationFailureAsync(
        Exception failure)
    {
        RecordOperationFailure(failure);
        await CompleteFailureCleanupAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _operationFinished, 1);
        return CreateOutcome();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Bounded cleanup continues after each failure so earlier evidence is never replaced.")]
    private async Task CompleteFailureCleanupAsync()
    {
        var cleanupSucceeded = true;
        try
        {
            _containment.Terminate();
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.TerminationCompleted,
                OwnedProcessFailurePhase.Termination));
        }
        catch (Exception failure)
        {
            cleanupSucceeded = false;
            RecordCleanupViolation(
                failure,
                OwnedProcessFailurePhase.Termination,
                OwnedProcessFailureKind.TerminationFailed);
        }

        try
        {
            await _commands.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupSucceeded = false;
            RecordCleanupViolation(
                failure,
                OwnedProcessFailurePhase.ResourceRelease,
                OwnedProcessFailureKind.ResourceReleaseFailed);
        }

        if (_containment.ObservationPoint == QuiescenceObservationPoint.BeforeAnchorReap)
        {
            cleanupSucceeded &= await TryAwaitCleanupQuiescenceAsync().ConfigureAwait(false);
        }

        try
        {
            await _budget.AwaitCleanupAsync(
                    _supervisor.WaitForExitAsync(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            MarkAnchorReaped();
        }
        catch (Exception failure)
        {
            cleanupSucceeded = false;
            RecordCleanupViolation(
                failure,
                OwnedProcessFailurePhase.Reap,
                failure is TimeoutException
                    ? OwnedProcessFailureKind.CleanupDeadlineExceeded
                    : OwnedProcessFailureKind.ReapFailed);
        }

        if (_containment.ObservationPoint == QuiescenceObservationPoint.AfterAnchorReap &&
            _anchorReaped)
        {
            cleanupSucceeded &= await TryAwaitCleanupQuiescenceAsync().ConfigureAwait(false);
        }

        try
        {
            await _budget.AwaitCleanupAsync(
                    Task.WhenAll(_standardOutputTask, _standardErrorTask),
                    CancellationToken.None)
                .ConfigureAwait(false);
            CaptureDrainedStreams();
        }
        catch (Exception failure)
        {
            cleanupSucceeded = false;
            var typed = CreateFailure(
                failure is TimeoutException
                    ? OwnedProcessFailureKind.StreamDrainDeadlineExceeded
                    : OwnedProcessFailureKind.ExecutionFailed,
                OwnedProcessFailurePhase.StreamDrain,
                OwnedProcessFailureChannel.Cleanup,
                failure);
            _proof.Violate(OwnedProcessInvariantKind.StreamDrain, typed);
            _proof.Violate(OwnedProcessInvariantKind.BoundedCleanup, typed);
        }

        cleanupSucceeded &= await ReleaseResourcesAsync().ConfigureAwait(false);
        if (cleanupSucceeded)
        {
            _proof.Prove(OwnedProcessInvariantKind.BoundedCleanup);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.CleanupCompleted,
                OwnedProcessFailurePhase.ResourceRelease));
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Each cleanup authority failure is retained independently.")]
    private async Task<bool> TryAwaitCleanupQuiescenceAsync()
    {
        try
        {
            while (true)
            {
                if (_containment.ObserveQuiescence() == ContainmentOccupancy.Quiescent)
                {
                    RecordTreeQuiescent();
                    if (_targetProcessId != null ||
                        GetInvariantState(OwnedProcessInvariantKind.TargetTerminal) ==
                            OwnedProcessInvariantState.Unknown)
                    {
                        _proof.Prove(OwnedProcessInvariantKind.TargetTerminal);
                        _proof.RecordFact(new OwnedProcessFact(
                            OwnedProcessFactKind.TargetTerminal,
                            OwnedProcessFailurePhase.Quiescence,
                            "authoritative-tree-quiescence"));
                    }
                    return true;
                }

                await _budget.DelayCleanupObservationAsync(
                        ObservationInterval,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            var kind = failure is TimeoutException
                ? OwnedProcessFailureKind.CleanupDeadlineExceeded
                : OwnedProcessFailureKind.ContainmentLost;
            var typed = CreateFailure(
                kind,
                OwnedProcessFailurePhase.Quiescence,
                OwnedProcessFailureChannel.Cleanup,
                failure);
            _proof.Violate(OwnedProcessInvariantKind.TreeQuiescence, typed);
            _proof.Violate(OwnedProcessInvariantKind.BoundedCleanup, typed);
            if (failure is ContainmentAuthorityException)
            {
                _proof.Violate(OwnedProcessInvariantKind.RequiredContainment, typed);
                _proof.RecordFact(new OwnedProcessFact(
                    OwnedProcessFactKind.ContainmentLost,
                    OwnedProcessFailurePhase.Quiescence,
                    failure.Message));
            }
            return false;
        }
    }

    private void RecordOperationFailure(Exception failure)
    {
        var typed = ClassifyFailure(
            failure,
            _operationPhase,
            OwnedProcessFailureChannel.Operation);
        _proof.Violate(OwnedProcessInvariantKind.OperationCompletion, typed);

        if (failure is TimeoutException || _budget.OperationExpired)
        {
            var deadline = CreateFailure(
                OwnedProcessFailureKind.OperationDeadlineExceeded,
                _operationPhase,
                OwnedProcessFailureChannel.Operation,
                failure);
            _proof.Violate(OwnedProcessInvariantKind.OperationBudget, deadline);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.OperationDeadlineExceeded,
                _operationPhase));
        }
        else
        {
            _proof.Prove(OwnedProcessInvariantKind.OperationBudget);
        }

        if (_operationPhase == OwnedProcessFailurePhase.StreamDrain)
        {
            _proof.Violate(OwnedProcessInvariantKind.StreamDrain, typed);
        }
        if (failure is ContainmentAuthorityException)
        {
            _proof.Violate(OwnedProcessInvariantKind.RequiredContainment, typed);
            _proof.Violate(OwnedProcessInvariantKind.TreeQuiescence, typed);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.ContainmentLost,
                _operationPhase,
                failure.Message));
        }
    }

    private void RecordObservedInterruptions()
    {
        if (Volatile.Read(ref _callerCancellationObserved) != 0)
        {
            var failure = new OwnedProcessFailure(
                OwnedProcessFailureKind.CallerCancelled,
                _operationPhase,
                OwnedProcessFailureChannel.Operation,
                nameof(OperationCanceledException),
                "The caller cancelled the owned process operation.");
            _proof.Violate(OwnedProcessInvariantKind.OperationCompletion, failure);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.CancellationRequested,
                _operationPhase));
        }

        if (Volatile.Read(ref _lifetimeInterruptionObserved) != 0)
        {
            var failure = new OwnedProcessFailure(
                OwnedProcessFailureKind.LifetimeClosed,
                _operationPhase,
                OwnedProcessFailureChannel.Operation,
                nameof(OperationCanceledException),
                "The ownership lifetime closed before operation completion.");
            _proof.Violate(OwnedProcessInvariantKind.OperationCompletion, failure);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.LifetimeCloseRequested,
                _operationPhase));
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All resource-release failures must be retained in the final proof snapshot.")]
    private async Task<bool> ReleaseResourcesAsync()
    {
        if (_resourcesReleased)
        {
            return true;
        }

        var succeeded = true;
        try
        {
            await _commands.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            succeeded = false;
            RecordResourceReleaseFailure(failure);
        }
        try
        {
            await _status.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            succeeded = false;
            RecordResourceReleaseFailure(failure);
        }
        try
        {
            _containment.Dispose();
        }
        catch (Exception failure)
        {
            succeeded = false;
            RecordResourceReleaseFailure(failure);
        }
        try
        {
            _supervisor.Dispose();
        }
        catch (Exception failure)
        {
            succeeded = false;
            RecordResourceReleaseFailure(failure);
        }
        try
        {
            _ownerSignal.Dispose();
        }
        catch (Exception failure)
        {
            succeeded = false;
            RecordResourceReleaseFailure(failure);
        }

        _resourcesReleased = true;
        if (succeeded)
        {
            _proof.Prove(OwnedProcessInvariantKind.OwnershipLifetime);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.OwnershipClosed,
                OwnedProcessFailurePhase.ResourceRelease));
        }
        return succeeded;
    }

    private void RecordResourceReleaseFailure(Exception failure)
    {
        var typed = CreateFailure(
            OwnedProcessFailureKind.ResourceReleaseFailed,
            OwnedProcessFailurePhase.ResourceRelease,
            OwnedProcessFailureChannel.Cleanup,
            failure);
        _proof.Violate(OwnedProcessInvariantKind.OwnershipLifetime, typed);
        _proof.Violate(OwnedProcessInvariantKind.BoundedCleanup, typed);
    }

    private void RecordCleanupViolation(
        Exception failure,
        OwnedProcessFailurePhase phase,
        OwnedProcessFailureKind kind)
    {
        var typed = CreateFailure(
            kind,
            phase,
            OwnedProcessFailureChannel.Cleanup,
            failure);
        _proof.Violate(OwnedProcessInvariantKind.BoundedCleanup, typed);
    }

    private OwnedProcessOutcome CreateOutcome()
    {
        var snapshot = _proof.Snapshot();
        foreach (var invariant in snapshot.Invariants.Where(
                     invariant => invariant.State == OwnedProcessInvariantState.Unknown))
        {
            _proof.RecordFailure(new OwnedProcessFailure(
                OwnedProcessFailureKind.RequiredInvariantUnknown,
                OwnedProcessFailurePhase.Unspecified,
                OwnedProcessFailureChannel.Operation,
                nameof(OwnedProcessInvariantState.Unknown),
                $"Required invariant {invariant.Kind} remains Unknown."));
        }
        snapshot = _proof.Snapshot();
        return new OwnedProcessOutcome(
            _supervisorProcessId,
            _targetProcessId,
            _exitCode,
            _standardOutput,
            _standardError,
            _targetExitObservedAtUnixMilliseconds,
            _ownership,
            snapshot.Invariants,
            snapshot.Facts,
            snapshot.Failures);
    }

    private OwnedProcessInvariantState GetInvariantState(OwnedProcessInvariantKind kind)
    {
        return _proof.Snapshot().Invariants.Single(invariant => invariant.Kind == kind).State;
    }

    private static OwnedProcessFailure ClassifyFailure(
        Exception failure,
        OwnedProcessFailurePhase phase,
        OwnedProcessFailureChannel channel)
    {
        var kind = failure switch
        {
            ContainmentAuthorityException authority
                when phase == OwnedProcessFailurePhase.OwnershipEstablishment =>
                    authority.Kind is ContainmentAuthorityFailureKind.UnsupportedPlatform or
                        ContainmentAuthorityFailureKind.AuthorityUnavailable
                        ? OwnedProcessFailureKind.ContainmentUnavailable
                        : OwnedProcessFailureKind.ContainmentSetupFailed,
            ContainmentAuthorityException => OwnedProcessFailureKind.ContainmentLost,
            TimeoutException when phase == OwnedProcessFailurePhase.StreamDrain =>
                OwnedProcessFailureKind.StreamDrainDeadlineExceeded,
            TimeoutException when channel == OwnedProcessFailureChannel.Cleanup =>
                OwnedProcessFailureKind.CleanupDeadlineExceeded,
            TimeoutException => OwnedProcessFailureKind.OperationDeadlineExceeded,
            OperationCanceledException => OwnedProcessFailureKind.ExecutionFailed,
            InvalidDataException => OwnedProcessFailureKind.SupervisorProtocolFailed,
            _ => OwnedProcessFailureKind.ExecutionFailed
        };
        return CreateFailure(kind, phase, channel, failure);
    }

    private static OwnedProcessFailure CreateFailure(
        OwnedProcessFailureKind kind,
        OwnedProcessFailurePhase phase,
        OwnedProcessFailureChannel channel,
        Exception failure)
    {
        return new OwnedProcessFailure(
            kind,
            phase,
            channel,
            failure.GetType().FullName ?? failure.GetType().Name,
            failure.Message);
    }
}
