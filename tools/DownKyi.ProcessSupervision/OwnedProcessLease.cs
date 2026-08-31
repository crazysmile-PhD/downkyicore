using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // Public process-supervision boundary is consumed by PowerShell and platform tests.
public sealed partial class OwnedProcessLease : IAsyncDisposable
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(25);
    private static readonly ProcessOwnershipMetadata UnestablishedOwnership = new(
        ProcessIdentityAuthority.Unspecified,
        ProcessContainmentKind.Unspecified,
        ProcessContainmentStrength.Unspecified,
        ProcessMembershipAuthority.Unspecified,
        "unavailable",
        "unavailable",
        "unavailable",
        OwnershipEstablished: false);

    private readonly TransitionBudget _budget;
    private readonly Process _supervisor;
    private readonly int _supervisorProcessId;
    private readonly IProcessContainmentLease _containment;
    private readonly NamedPipeServerStream _commands;
    private readonly NamedPipeServerStream _status;
    private readonly Task<string> _standardOutputTask;
    private readonly Task<string> _standardErrorTask;
    private readonly OwnedProcessProofAccumulator _proof = new();
    private readonly OwnedProcessCompletionGate _completion = new();
    private readonly CancellationTokenSource _ownerSignal = new();
    private ProcessOwnershipMetadata _ownership = UnestablishedOwnership;
    private int? _targetProcessId;
    private int? _exitCode;
    private long? _targetExitObservedAtUnixMilliseconds;
    private string _standardOutput = string.Empty;
    private string _standardError = string.Empty;
    private int _callerCancellationObserved;
    private int _lifetimeInterruptionObserved;
    private int _operationFinished;
    private bool _anchorReaped;
    private bool _streamsDrained;
    private bool _resourcesReleased;
    private OwnedProcessFailurePhase _operationPhase = OwnedProcessFailurePhase.TargetExecution;

    private OwnedProcessLease(
        TransitionBudget budget,
        Process supervisor,
        IProcessContainmentLease containment,
        NamedPipeServerStream commands,
        NamedPipeServerStream status,
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        _budget = budget;
        _supervisor = supervisor;
        _supervisorProcessId = supervisor.Id;
        _containment = containment;
        _commands = commands;
        _status = status;
        _standardOutputTask = standardOutputTask;
        _standardErrorTask = standardErrorTask;
    }

    public int SupervisorProcessId => _supervisorProcessId;

    public int TargetProcessId => _targetProcessId ?? throw new InvalidOperationException(
        "The target process has not been authorized.");

    public ProcessOwnershipMetadata Ownership => _ownership;

    public async Task<OwnedProcessOutcome> WaitAsync(
        CancellationToken cancellationToken = default)
    {
        var decision = _completion.BeginWait();
        if (decision.StartsOwner)
        {
            _ = RunOwnerAsync(cancellationToken);
        }

        var outcome = await decision.Completion.ConfigureAwait(false);
        return outcome.FormalGatePassed
            ? outcome
            : throw new OwnedProcessExecutionException(outcome);
    }

    public async ValueTask DisposeAsync()
    {
        var decision = _completion.RequestLifetimeClose();
        if (Volatile.Read(ref _operationFinished) == 0)
        {
            Interlocked.Exchange(ref _lifetimeInterruptionObserved, 1);
        }
        if (decision.SignalOwner)
        {
            await _ownerSignal.CancelAsync().ConfigureAwait(false);
        }
        if (decision.StartsOwner)
        {
            _ = RunOwnerAsync(CancellationToken.None);
        }

        var outcome = await decision.Completion.ConfigureAwait(false);
        if (decision.StartsOwner && !outcome.FormalGatePassed)
        {
            throw new OwnedProcessExecutionException(outcome);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The single owner must convert every operation and cleanup failure into retained typed proof.")]
    private async Task RunOwnerAsync(CancellationToken callerCancellationToken)
    {
        var registration = callerCancellationToken.UnsafeRegister(
            static state => ((OwnedProcessLease)state!).SignalCallerCancellation(),
            this);
        var operationSucceeded = false;
        try
        {
            if (_completion.LifetimeCloseRequested)
            {
                Interlocked.Exchange(ref _lifetimeInterruptionObserved, 1);
                throw new OperationCanceledException("Ownership lifetime close was requested.");
            }

            _operationPhase = OwnedProcessFailurePhase.TargetExecution;
            var exited = await ReadOperationFrameAsync<TargetExitedFrame>(_ownerSignal.Token)
                .ConfigureAwait(false);
            _exitCode = exited.Exited.ExitCode;
            _targetExitObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.TargetTerminal,
                OwnedProcessFailurePhase.TargetExecution,
                "direct-child-wait"));
            _proof.Prove(OwnedProcessInvariantKind.TargetTerminal);

            if (_containment.ObservationPoint == QuiescenceObservationPoint.BeforeAnchorReap)
            {
                _operationPhase = OwnedProcessFailurePhase.Quiescence;
                await AwaitOperationQuiescenceAsync(_ownerSignal.Token).ConfigureAwait(false);
            }

            _operationPhase = OwnedProcessFailurePhase.SupervisorFinalization;
            await WriteOperationFrameAsync(new FinalizeFrame(), _ownerSignal.Token)
                .ConfigureAwait(false);
            _ = await ReadOperationFrameAsync<FinalizedFrame>(_ownerSignal.Token)
                .ConfigureAwait(false);
            _operationPhase = OwnedProcessFailurePhase.Reap;
            await ReapSupervisorOperationAsync(_ownerSignal.Token).ConfigureAwait(false);

            if (_containment.ObservationPoint == QuiescenceObservationPoint.AfterAnchorReap)
            {
                _operationPhase = OwnedProcessFailurePhase.Quiescence;
                await AwaitOperationQuiescenceAsync(_ownerSignal.Token).ConfigureAwait(false);
            }

            _operationPhase = OwnedProcessFailurePhase.StreamDrain;
            await DrainStreamsOperationAsync(_ownerSignal.Token).ConfigureAwait(false);
            _proof.Prove(OwnedProcessInvariantKind.OperationCompletion);
            _proof.Prove(OwnedProcessInvariantKind.OperationBudget);
            Interlocked.Exchange(ref _operationFinished, 1);
            operationSucceeded = true;
        }
        catch (Exception failure)
        {
            RecordOperationFailure(failure);
        }

        if (callerCancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _callerCancellationObserved, 1);
        }
        await registration.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (operationSucceeded)
            {
                if (await ReleaseResourcesAsync().ConfigureAwait(false))
                {
                    _proof.Prove(OwnedProcessInvariantKind.BoundedCleanup);
                    _proof.RecordFact(new OwnedProcessFact(
                        OwnedProcessFactKind.CleanupCompleted,
                        OwnedProcessFailurePhase.ResourceRelease));
                }
            }
            else
            {
                await CompleteFailureCleanupAsync().ConfigureAwait(false);
            }
        }
        catch (Exception cleanupFailure)
        {
            _proof.Violate(
                OwnedProcessInvariantKind.BoundedCleanup,
                ClassifyFailure(
                    cleanupFailure,
                    OwnedProcessFailurePhase.ResourceRelease,
                    OwnedProcessFailureChannel.Cleanup));
        }
        finally
        {
            Interlocked.Exchange(ref _operationFinished, 1);
            RecordObservedInterruptions();
            var outcome = CreateOutcome();
            _completion.TryPublish(outcome);
        }
    }

    private void SignalCallerCancellation()
    {
        if (Volatile.Read(ref _operationFinished) != 0)
        {
            return;
        }

        _ownerSignal.Cancel();
    }

    private async Task AwaitOperationQuiescenceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!_anchorReaped)
            {
                _containment.AssertAnchorOwned(_supervisor);
            }
            if (_containment.ObserveQuiescence() == ContainmentOccupancy.Quiescent)
            {
                RecordTreeQuiescent();
                return;
            }

            await _budget.DelayOperationObservationAsync(
                    ObservationInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReapSupervisorOperationAsync(CancellationToken cancellationToken)
    {
        await _budget.AwaitOperationAsync(
                _supervisor.WaitForExitAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        MarkAnchorReaped();
    }

    private async Task DrainStreamsOperationAsync(CancellationToken cancellationToken)
    {
        await _budget.AwaitOperationAsync(
                Task.WhenAll(_standardOutputTask, _standardErrorTask),
                cancellationToken)
            .ConfigureAwait(false);
        CaptureDrainedStreams();
    }

    private void RecordTreeQuiescent()
    {
        _proof.Prove(OwnedProcessInvariantKind.TreeQuiescence);
        _proof.RecordFact(new OwnedProcessFact(
            OwnedProcessFactKind.TreeQuiescent,
            OwnedProcessFailurePhase.Quiescence,
            _containment.Metadata.MembershipAuthority.ToString()));
    }

    private void MarkAnchorReaped()
    {
        if (_anchorReaped)
        {
            return;
        }

        _containment.MarkAnchorReaped();
        _anchorReaped = true;
        _proof.RecordFact(new OwnedProcessFact(
            OwnedProcessFactKind.ReapCompleted,
            OwnedProcessFailurePhase.Reap,
            "direct-supervisor-wait"));
    }

    private void CaptureDrainedStreams()
    {
        if (_streamsDrained)
        {
            return;
        }

        _standardOutput = _standardOutputTask.GetAwaiter().GetResult();
        _standardError = _standardErrorTask.GetAwaiter().GetResult();
        _streamsDrained = true;
        _proof.Prove(OwnedProcessInvariantKind.StreamDrain);
        _proof.RecordFact(new OwnedProcessFact(
            OwnedProcessFactKind.StreamsDrained,
            OwnedProcessFailurePhase.StreamDrain));
    }
}
