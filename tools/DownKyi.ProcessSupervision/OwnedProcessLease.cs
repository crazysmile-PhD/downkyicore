using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports the lease API.

public sealed class OwnedProcessLease : IAsyncDisposable
{
    private const byte OwnershipAttachment = 0xB1;
    private const byte LaunchAuthorization = 0xC1;
    private const byte OwnershipEstablished = 0xA1;
    private const byte OwnershipMutationActive = 0xA2;
    private const byte TargetStarted = 0xA3;
    private const byte TargetExited = 0xA4;
    private const byte FinalizeSupervisor = 0xD1;
    private const int MaximumLaunchPayloadBytes = 1024 * 1024;

    private readonly Process _supervisor;
    private readonly IProcessContainmentLease _containment;
    private readonly NamedPipeServerStream _control;
    private readonly NamedPipeServerStream _status;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private readonly ProcessStreamObservation _standardOutputObservation;
    private readonly ProcessStreamObservation _standardErrorObservation;
    private readonly Task<TargetExitReport?> _targetExitReport;
    private readonly CancellationTokenSource _targetExited;
    private readonly EvidenceHoldCoordinator? _evidenceHold;
    private readonly TransitionBudget _budget;
    private readonly ProcessOwnershipMutation _mutation;
    private int _completionState;
    private int _ownerLifetimeClosed;
    private int _reapFailureInjected;
    private TimeSpan? _reapedAfter;
    private TimeSpan? _failureTerminationBeganAfter;
    private TimeSpan? _failureTerminationCompletedAfter;
    private TimeSpan? _failureTreeQuiescenceBeganAfter;
    private TimeSpan? _failureTreeQuiescenceCompletedAfter;
    private TimeSpan? _failureSupervisorReapBeganAfter;
    private TimeSpan? _failureSupervisorReapCompletedAfter;
    private TimeSpan? _failureStreamDrainBeganAfter;
    private TimeSpan? _failureStreamDrainCompletedAfter;

    private OwnedProcessLease(
        Process supervisor,
        IProcessContainmentLease containment,
        NamedPipeServerStream control,
        NamedPipeServerStream status,
        Task<string> standardOutput,
        Task<string> standardError,
        ProcessStreamObservation standardOutputObservation,
        ProcessStreamObservation standardErrorObservation,
        Task<TargetExitReport?> targetExitReport,
        CancellationTokenSource targetExited,
        EvidenceHoldCoordinator? evidenceHold,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        int targetProcessId,
        ProcessOwnershipMetadata ownership)
    {
        _supervisor = supervisor;
        _containment = containment;
        _control = control;
        _status = status;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _standardOutputObservation = standardOutputObservation;
        _standardErrorObservation = standardErrorObservation;
        _targetExitReport = targetExitReport;
        _targetExited = targetExited;
        _evidenceHold = evidenceHold;
        _budget = budget;
        _mutation = mutation;
        TargetProcessId = targetProcessId;
        Ownership = ownership;
    }

    public ProcessOwnershipMetadata Ownership { get; }

    public int SupervisorProcessId => _supervisor.Id;

    public int TargetProcessId { get; }

    public CancellationToken TargetExitedToken => _targetExited.Token;

    internal TimeSpan? StandardOutputFirstObservedAfter =>
        _standardOutputObservation.FirstObservedAfter;

    internal TimeSpan? StandardErrorFirstObservedAfter =>
        _standardErrorObservation.FirstObservedAfter;

    internal TimeSpan? StandardOutputDrainedAfter =>
        _standardOutputObservation.DrainedAfter;

    internal TimeSpan? StandardErrorDrainedAfter =>
        _standardErrorObservation.DrainedAfter;

    internal TimeSpan? ReapedAfter => _reapedAfter;

    internal TimeSpan? FailureTerminationBeganAfter =>
        _failureTerminationBeganAfter;

    internal TimeSpan? FailureTerminationCompletedAfter =>
        _failureTerminationCompletedAfter;

    internal TimeSpan? FailureTreeQuiescenceBeganAfter =>
        _failureTreeQuiescenceBeganAfter;

    internal TimeSpan? FailureTreeQuiescenceCompletedAfter =>
        _failureTreeQuiescenceCompletedAfter;

    internal TimeSpan? FailureSupervisorReapBeganAfter =>
        _failureSupervisorReapBeganAfter;

    internal TimeSpan? FailureSupervisorReapCompletedAfter =>
        _failureSupervisorReapCompletedAfter;

    internal TimeSpan? FailureStreamDrainBeganAfter =>
        _failureStreamDrainBeganAfter;

    internal TimeSpan? FailureStreamDrainCompletedAfter =>
        _failureStreamDrainCompletedAfter;

    internal TimeSpan? ObservedTargetExitedAfter =>
        _targetExitReport.IsCompletedSuccessfully
            ? _targetExitReport.Result?.ExitedAfter
            : null;

    public EvidenceHoldOutcome EvidenceHold =>
        _evidenceHold?.Snapshot ?? EvidenceHoldOutcome.CreateNotRequested();

    public static Task<OwnedProcessLease> StartAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest: null,
            ProcessOwnershipMutation.None,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline: null,
            startFailureObservedForTesting: null,
            cancellationToken);
    }

    public static Task<OwnedProcessLease> StartAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        EvidenceHoldRequest evidenceHoldRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceHoldRequest);
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest,
            ProcessOwnershipMutation.None,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline: null,
            startFailureObservedForTesting: null,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartForTestingAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest: null,
            mutation,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline: null,
            startFailureObservedForTesting: null,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartForTestingAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        Action startFailureObservedForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startFailureObservedForTesting);
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest: null,
            mutation,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline: null,
            startFailureObservedForTesting,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartForTestingAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        EvidenceHoldRequest evidenceHoldRequest,
        ProcessOwnershipMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceHoldRequest);
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest,
            mutation,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline: null,
            startFailureObservedForTesting: null,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartForTestingAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        EvidenceHoldRequest evidenceHoldRequest,
        ProcessOwnershipMutation mutation,
        Task acknowledgmentPublicationGateForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceHoldRequest);
        ArgumentNullException.ThrowIfNull(acknowledgmentPublicationGateForTesting);
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest,
            mutation,
            acknowledgmentPublicationGateForTesting,
            startTimeline: null,
            startFailureObservedForTesting: null,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartObservedAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        OwnedProcessStartTimeline startTimeline,
        Action? startFailureObservedForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startTimeline);
        return StartCoreAsync(
            launchSpec,
            budget,
            evidenceHoldRequest: null,
            mutation,
            acknowledgmentPublicationGateForTesting: null,
            startTimeline,
            startFailureObservedForTesting,
            cancellationToken);
    }

    internal void CloseOwnerLifetimeForTesting()
    {
        CloseOwnerLifetime();
    }

    internal async Task<long> WaitForTargetExitForTestingAsync()
    {
        var report = await _targetExitReport.ConfigureAwait(false);
        return report?.ExitedAtUnixMilliseconds
            ?? throw new InvalidOperationException("The target exited without an authoritative report.");
    }

    internal Task WaitForEvidenceHoldOutcomeSnapshotForTestingAsync()
    {
        return _evidenceHold?.OutcomeSnapshotStartedForTesting
            ?? throw new InvalidOperationException("This owned process lease has no evidence hold.");
    }

    public Task CompleteEvidenceHoldAsync(
        EvidenceCaptureCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (_evidenceHold == null)
        {
            throw new InvalidOperationException("This owned process lease has no evidence hold.");
        }
        if (completion is not (EvidenceCaptureCompletion.Captured or
            EvidenceCaptureCompletion.Failed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                "Evidence capture must complete as captured or failed.");
        }

        return _evidenceHold.CompleteAsync(
            completion,
            _budget,
            cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease owner must preserve any execution failure while attempting bounded cleanup.")]
    public async Task<OwnedProcessOutcome> WaitAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The owned process lease has already completed.");
        }

        OwnedProcessOutcome? outcome = null;
        Exception? operationFailure = null;
        var pendingFailureKind = OwnedProcessFailureKind.ExecutionFailed;
        var supervisorProcessId = SupervisorProcessId;
        var treeQuiescenceProven = false;
        TargetExitReport? targetExit = null;
        try
        {
            pendingFailureKind = OwnedProcessFailureKind.OperationDeadlineExceeded;
            targetExit = await WaitWithBudgetAsync(
                    _targetExitReport,
                    _budget.RemainingOperation,
                    "The owned target did not exit before the operation deadline.",
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The process supervisor exited without an authoritative target-exit report.");

            if (_containment.MembershipRequiresAnchorExit)
            {
                await FinalizeSupervisorAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_mutation.HasFlag(ProcessOwnershipMutation.ReleaseAnchorBeforeMembership))
            {
                if (!_containment.MembershipRequiresAnchorExit)
                {
                    await FinalizeSupervisorAsync(cancellationToken).ConfigureAwait(false);
                }
                await WaitForSupervisorExitAsync(
                        useCleanupBudget: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            pendingFailureKind = OwnedProcessFailureKind.OwnedTreeNotQuiescent;
            await WaitForTreeQuiescenceAsync(
                    useCleanupBudget: false,
                    cancellationToken)
                .ConfigureAwait(false);
            treeQuiescenceProven = true;

            if (!_containment.MembershipRequiresAnchorExit)
            {
                await FinalizeSupervisorAsync(cancellationToken).ConfigureAwait(false);
            }

            pendingFailureKind = OwnedProcessFailureKind.OperationDeadlineExceeded;
            await WaitForSupervisorExitAsync(
                    useCleanupBudget: false,
                    cancellationToken)
                .ConfigureAwait(false);

            pendingFailureKind = OwnedProcessFailureKind.StreamDrainDeadlineExceeded;
            await WaitWithBudgetAsync(
                    Task.WhenAll(_standardOutput, _standardError),
                    _budget.RemainingOperation,
                    "Owned process streams did not drain before the operation deadline.",
                    cancellationToken)
                .ConfigureAwait(false);

            pendingFailureKind = OwnedProcessFailureKind.OperationDeadlineExceeded;
            var evidenceHold = _evidenceHold == null
                ? EvidenceHoldOutcome.CreateNotRequested()
                : await _evidenceHold.SnapshotForOutcomeAsync(
                        _budget,
                        useCleanupBudget: false,
                        cancellationToken)
                    .ConfigureAwait(false);

            outcome = new OwnedProcessOutcome(
                supervisorProcessId,
                TargetProcessId,
                targetExit.ExitCode,
                await _standardOutput.ConfigureAwait(false),
                await _standardError.ConfigureAwait(false),
                targetExit.ExitedAtUnixMilliseconds,
                targetExit.ExitedAfter,
                TreeQuiescent: true,
                Ownership,
                evidenceHold);
        }
        catch (Exception failure)
        {
            operationFailure = failure;
            if (failure is OperationCanceledException)
            {
                pendingFailureKind = OwnedProcessFailureKind.CallerCancelled;
            }
            else if (failure is not TimeoutException)
            {
                pendingFailureKind = OwnedProcessFailureKind.ExecutionFailed;
            }
        }

        if (operationFailure != null)
        {
            var cleanupFailure = await CaptureFailureAsync(TerminateAndReapAsync)
                .ConfigureAwait(false);
            var cleanupFailures = cleanupFailure == null
                ? new List<Exception>()
                : FlattenFailures(cleanupFailure).ToList();
            var evidenceHold = EvidenceHold;
            if (_evidenceHold != null)
            {
                try
                {
                    evidenceHold = await _evidenceHold.SnapshotForOutcomeAsync(
                            _budget,
                            useCleanupBudget: true,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    AddCleanupFailure(
                        cleanupFailures,
                        OwnedProcessCleanupStage.Dispose,
                        failure);
                    evidenceHold = EvidenceHold;
                }
            }

            var standardOutput = cleanupFailures.Count == 0
                ? await _standardOutput.ConfigureAwait(false)
                : ReadCompletedOutput(_standardOutput);
            var standardError = cleanupFailures.Count == 0
                ? await _standardError.ConfigureAwait(false)
                : ReadCompletedOutput(_standardError);
            throw new OwnedProcessExecutionException(
                new OwnedProcessFailure(
                    pendingFailureKind,
                    supervisorProcessId,
                    TargetProcessId,
                    standardOutput,
                    standardError,
                    targetExit?.ExitedAtUnixMilliseconds,
                    targetExit?.ExitedAfter,
                    TreeQuiescent: treeQuiescenceProven,
                    Ownership,
                    evidenceHold),
                operationFailure,
                cleanupFailures);
        }

        ReleaseResources();
        return outcome
            ?? throw new InvalidOperationException("The owned process outcome is unavailable.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _completionState, 2, 0) == 0)
        {
            await TerminateAndReapAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The launch owner must preserve any launch failure while attempting every containment cleanup stage.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Successful launch transfers every disposable owner to the lease; failed launch releases them before propagating.")]
    private static async Task<OwnedProcessLease> StartCoreAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        EvidenceHoldRequest? evidenceHoldRequest,
        ProcessOwnershipMutation mutation,
        Task? acknowledgmentPublicationGateForTesting,
        OwnedProcessStartTimeline? startTimeline,
        Action? startFailureObservedForTesting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (evidenceHoldRequest != null &&
            launchSpec.Environment.ContainsKey(evidenceHoldRequest.TargetEnvironmentVariable))
        {
            throw new ArgumentException(
                "The launch specification cannot provide the supervisor-owned evidence-hold variable.",
                nameof(launchSpec));
        }

        var controlPipeName = IpcEndpointName.Create("OwnedProcessLease.Control");
        var statusPipeName = IpcEndpointName.Create("OwnedProcessLease.Status");
        var control = new NamedPipeServerStream(
            controlPipeName.PhysicalIdentifier,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var status = new NamedPipeServerStream(
            statusPipeName.PhysicalIdentifier,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var jobName = $"Local\\DownKyi.ProcessLease.{Guid.NewGuid():N}";
        var evidenceHold = evidenceHoldRequest == null
            ? null
            : new EvidenceHoldCoordinator(
                evidenceHoldRequest,
                acknowledgmentPublicationGateForTesting);
        var targetExited = new CancellationTokenSource();
        var startInfo = CreateSupervisorStartInfo(
            controlPipeName.PhysicalIdentifier,
            statusPipeName.PhysicalIdentifier,
            jobName,
            mutation);
        var supervisor = new Process { StartInfo = startInfo };
        IProcessContainmentLease? containment = null;
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var standardOutputObservation = new ProcessStreamObservation(budget);
        var standardErrorObservation = new ProcessStreamObservation(budget);
        var started = false;
        var processOwnershipEstablished = false;
        try
        {
            var supervisorStarted = supervisor.Start();
            startTimeline?.Mark(OwnedProcessStartTransition.SupervisorProcessStartReturned);
            if (!supervisorStarted)
            {
                throw new InvalidOperationException("The process supervisor did not start.");
            }

            started = true;
            startTimeline?.SetSupervisorProcessId(supervisor.Id);
            evidenceHold?.ReleaseLocalClientHandles();
            standardOutput = ReadToEndObservedAsync(
                supervisor.StandardOutput,
                standardOutputObservation);
            standardError = ReadToEndObservedAsync(
                supervisor.StandardError,
                standardErrorObservation);
            if (mutation.HasFlag(ProcessOwnershipMutation.StallStreamDrain))
            {
                standardOutput = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
                standardError = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
            containment = PlatformProcessContainment.Prepare(supervisor, jobName);
            startTimeline?.Mark(OwnedProcessStartTransition.ContainmentPrepared);
            containment.Establish(supervisor, mutation);
            startTimeline?.Mark(OwnedProcessStartTransition.ContainmentEstablished);
            containment = PlatformProcessContainment.ApplyFailureMutations(
                containment,
                mutation);
            var controlConnection = ObservePipeConnectionAsync(
                control.WaitForConnectionAsync(cancellationToken),
                startTimeline,
                OwnedProcessStartTransition.ControlPipeConnectionCompleted);
            var statusConnection = ObservePipeConnectionAsync(
                status.WaitForConnectionAsync(cancellationToken),
                startTimeline,
                OwnedProcessStartTransition.StatusPipeConnectionCompleted);
            await WaitWithBudgetAsync(
                    Task.WhenAll(controlConnection, statusConnection),
                    budget.RemainingOperation,
                    "The process supervisor did not connect its control channels before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);

            var attachment = JsonSerializer.SerializeToUtf8Bytes(
                new OwnershipAttachmentPayload(
                    containment.Metadata.ContainmentId,
                    containment.Metadata.MembershipId,
                    containment.Metadata.OwnerLifetimeId));
            await WriteFrameWithBudgetAsync(
                    control,
                    OwnershipAttachment,
                    attachment,
                    budget.RemainingOperation,
                    "The ownership attachment did not reach the inert supervisor before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);

            var ownershipStatus = await ReadByteWithBudgetAsync(
                    status,
                    budget,
                    "The supervisor did not acknowledge ownership before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
            startTimeline?.Mark(OwnedProcessStartTransition.OwnershipAcknowledgementReceived);
            if (ownershipStatus is not (OwnershipEstablished or OwnershipMutationActive))
            {
                throw new InvalidOperationException(
                    "The process supervisor refused target authorization without established ownership.");
            }
            processOwnershipEstablished =
                ownershipStatus == OwnershipEstablished &&
                containment.Metadata.OwnershipEstablished;

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                LaunchSpecPayload.FromLaunchSpec(launchSpec, evidenceHold));
            if (payload.Length > MaximumLaunchPayloadBytes)
            {
                throw new InvalidOperationException("The immutable launch specification is too large.");
            }

            await WriteFrameWithBudgetAsync(
                    control,
                    LaunchAuthorization,
                    payload,
                    budget.RemainingOperation,
                    "The immutable launch specification did not reach the supervisor before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
            startTimeline?.Mark(OwnedProcessStartTransition.LaunchAuthorizationWritten);

            var targetProcessId = await ReadTargetStartedAsync(
                    status,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            startTimeline?.SetTargetProcessId(targetProcessId);
            startTimeline?.Mark(OwnedProcessStartTransition.TargetStartAcknowledgementReceived);
            evidenceHold?.Grant();
            var targetExitReport = ReadTargetExitReportAsync(
                status,
                budget,
                targetExited);
            var ownership = containment.Metadata with
            {
                OwnershipEstablished =
                    processOwnershipEstablished
            };
            return new OwnedProcessLease(
                supervisor,
                containment,
                control,
                status,
                standardOutput,
                standardError,
                standardOutputObservation,
                standardErrorObservation,
                targetExitReport,
                targetExited,
                evidenceHold,
                budget,
                mutation,
                targetProcessId,
                ownership);
        }
        catch (Exception startFailure)
        {
            startTimeline?.Mark(OwnedProcessStartTransition.StartFailureObserved);
            if (startFailure is TimeoutException ||
                budget.RemainingOperation <= TimeSpan.Zero)
            {
                startTimeline?.MarkOperationDeadlineExhausted();
                startTimeline?.MarkOperationDeadlineExhaustionObserved();
            }
            var failures = new Collection<Exception> { startFailure };
            startFailureObservedForTesting?.Invoke();
            if (containment != null && processOwnershipEstablished)
            {
                startTimeline?.Mark(
                    OwnedProcessStartTransition.StartFailureTerminationBegan);
                try
                {
                    containment.Terminate();
                }
                catch (Exception cleanupFailure)
                {
                    AddCleanupFailure(
                        failures,
                        OwnedProcessCleanupStage.Terminate,
                        cleanupFailure);
                }
                finally
                {
                    startTimeline?.Mark(
                        OwnedProcessStartTransition.StartFailureTerminationCompleted);
                }

                startTimeline?.Mark(
                    OwnedProcessStartTransition.StartFailureTreeQuiescenceBegan);
                try
                {
                    await WaitForTreeQuiescenceAsync(
                            containment,
                            budget,
                            useCleanupBudget: true,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    AddCleanupFailure(
                        failures,
                        OwnedProcessCleanupStage.TreeQuiescence,
                        cleanupFailure);
                }
                finally
                {
                    startTimeline?.Mark(
                        OwnedProcessStartTransition.StartFailureTreeQuiescenceCompleted);
                }
            }
            else if (started)
            {
                startTimeline?.Mark(
                    OwnedProcessStartTransition.StartFailureTerminationBegan);
                try
                {
                    supervisor.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception cleanupFailure)
                {
                    AddCleanupFailure(
                        failures,
                        OwnedProcessCleanupStage.Terminate,
                        cleanupFailure);
                }
                finally
                {
                    startTimeline?.Mark(
                        OwnedProcessStartTransition.StartFailureTerminationCompleted);
                }
            }

            if (started)
            {
                startTimeline?.Mark(
                    OwnedProcessStartTransition.StartFailureSupervisorReapBegan);
            }
            try
            {
                if (started)
                {
                    await WaitWithBudgetAsync(
                            supervisor.WaitForExitAsync(CancellationToken.None),
                            budget.RemainingCleanup,
                            "The failed process supervisor did not terminate before its hard deadline.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    containment?.MarkAnchorReaped();
                }
            }
            catch (Exception cleanupFailure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.Reap,
                    cleanupFailure);
            }
            finally
            {
                if (started)
                {
                    startTimeline?.Mark(
                        OwnedProcessStartTransition.StartFailureSupervisorReapCompleted);
                }
            }

            if (standardOutput != null && standardError != null)
            {
                startTimeline?.Mark(
                    OwnedProcessStartTransition.StartFailureStreamDrainBegan);
            }
            try
            {
                if (standardOutput != null && standardError != null)
                {
                    await WaitWithBudgetAsync(
                            Task.WhenAll(standardOutput, standardError),
                            budget.RemainingCleanup,
                            "Failed-launch process streams did not drain before the hard deadline.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.StreamDrain,
                    cleanupFailure);
            }
            finally
            {
                if (standardOutput != null && standardError != null)
                {
                    startTimeline?.Mark(
                        OwnedProcessStartTransition.StartFailureStreamDrainCompleted);
                }
            }

            foreach (var resource in new IDisposable?[]
                     {
                         evidenceHold,
                         targetExited,
                         containment,
                         control,
                         status,
                         supervisor
                     })
            {
                try
                {
                    resource?.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    AddCleanupFailure(
                        failures,
                        OwnedProcessCleanupStage.Dispose,
                        cleanupFailure);
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(startFailure).Throw();
            }

            throw new AggregateException(
                "Process launch and ownership cleanup both failed.",
                failures);
        }
    }

    private static async Task ObservePipeConnectionAsync(
        Task connection,
        OwnedProcessStartTimeline? startTimeline,
        OwnedProcessStartTransition transition)
    {
        await connection.ConfigureAwait(false);
        startTimeline?.Mark(transition);
    }

    internal static ProcessStartInfo CreateSupervisorStartInfo(
        string controlPipeName,
        string statusPipeName,
        string jobName,
        ProcessOwnershipMutation mutation)
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var supervisorDirectory = Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException("The supervisor directory is unavailable.");
        var useDirectAppHost = OperatingSystem.IsWindows();
        var supervisorFileName = useDirectAppHost
            ? Path.ChangeExtension(assemblyPath, ".exe")
            : "dotnet";
        if (useDirectAppHost && !File.Exists(supervisorFileName))
        {
            throw new InvalidOperationException(
                $"The compiled process supervisor apphost was not found: {supervisorFileName}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = supervisorFileName,
            WorkingDirectory = supervisorDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!useDirectAppHost)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }
        startInfo.ArgumentList.Add(SupervisorHost.HostArgument);
        startInfo.ArgumentList.Add(controlPipeName);
        startInfo.ArgumentList.Add(statusPipeName);
        startInfo.ArgumentList.Add(jobName);
        startInfo.ArgumentList.Add(((int)mutation).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static async Task<int> ReadTargetStartedAsync(
        Stream status,
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        var payload = await ReadExactWithBudgetAsync(
                status,
                sizeof(byte) + sizeof(int),
                budget.RemainingOperation,
                "The supervisor did not report target launch before the deadline.",
                cancellationToken)
            .ConfigureAwait(false);
        if (payload[0] != TargetStarted)
        {
            throw new InvalidOperationException("The process supervisor returned an invalid launch state.");
        }

        var targetProcessId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(byte)));
        if (targetProcessId <= 0)
        {
            throw new InvalidOperationException("The process supervisor returned an invalid target identity.");
        }

        return targetProcessId;
    }

    private static async Task<TargetExitReport?> ReadTargetExitReportAsync(
        Stream status,
        TransitionBudget budget,
        CancellationTokenSource targetExited)
    {
        var payload = await ReadExactOrEofAsync(
                status,
                sizeof(byte) + sizeof(int) + sizeof(long),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (payload == null)
        {
            return null;
        }
        if (payload[0] != TargetExited)
        {
            throw new InvalidOperationException("The process supervisor returned an invalid target-exit state.");
        }

        var exitCode = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(byte)));
        var exitedAt = BinaryPrimitives.ReadInt64LittleEndian(
            payload.AsSpan(sizeof(byte) + sizeof(int)));
        if (exitedAt <= 0)
        {
            throw new InvalidOperationException("The target-exit timestamp is invalid.");
        }

        var exitedAfter = budget.Elapsed;
        await targetExited.CancelAsync().ConfigureAwait(false);
        return new TargetExitReport(exitCode, exitedAt, exitedAfter);
    }

    private async Task WaitForSupervisorExitAsync(
        bool useCleanupBudget,
        CancellationToken cancellationToken)
    {
        if (useCleanupBudget &&
            _mutation.HasFlag(ProcessOwnershipMutation.StallRootReap))
        {
            await WaitWithBudgetAsync(
                    Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None),
                    _budget.RemainingCleanup,
                    "The process supervisor did not reap before its hard deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await WaitWithBudgetAsync(
                _supervisor.WaitForExitAsync(cancellationToken),
                useCleanupBudget ? _budget.RemainingCleanup : _budget.RemainingOperation,
                useCleanupBudget
                    ? "The process supervisor did not reap before its hard deadline."
                    : "The process supervisor did not exit before the operation deadline.",
                cancellationToken)
            .ConfigureAwait(false);
        _containment.MarkAnchorReaped();
        _reapedAfter = _budget.Elapsed;
        if (_mutation.HasFlag(ProcessOwnershipMutation.FailAfterRootReap) &&
            Interlocked.Exchange(ref _reapFailureInjected, 1) == 0)
        {
            throw new InvalidOperationException("Injected root reap failure.");
        }
    }

    private Task WaitForTreeQuiescenceAsync(
        bool useCleanupBudget,
        CancellationToken cancellationToken)
    {
        return WaitForTreeQuiescenceAsync(
            _containment,
            _budget,
            useCleanupBudget,
            cancellationToken);
    }

    private static async Task WaitForTreeQuiescenceAsync(
        IProcessContainmentLease containment,
        TransitionBudget budget,
        bool useCleanupBudget,
        CancellationToken cancellationToken)
    {
        while (!containment.IsTreeQuiescent())
        {
            var remaining = useCleanupBudget
                ? budget.RemainingCleanup
                : budget.RemainingOperation;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    useCleanupBudget
                        ? "The owned process tree did not become quiescent before its hard deadline."
                        : "The owned process tree did not become quiescent before the operation deadline.");
            }

            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(20)
                        ? remaining
                        : TimeSpan.FromMilliseconds(20),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease owner must attempt every cleanup stage and preserve all concurrent failures.")]
    private async Task TerminateAndReapAsync()
    {
        var failures = new Collection<Exception>();
        var ownershipEstablished = Ownership.OwnershipEstablished;
        _failureTerminationBeganAfter ??= _budget.Elapsed;
        if (ownershipEstablished)
        {
            try
            {
                if (!_containment.IsTreeQuiescent())
                {
                    _containment.Terminate();
                }
            }
            catch (Exception failure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.Terminate,
                    failure);
                try
                {
                    _containment.Terminate();
                }
                catch (Exception terminationFailure)
                {
                    AddCleanupFailure(
                        failures,
                        OwnedProcessCleanupStage.Terminate,
                        terminationFailure);
                }
            }

            _failureTerminationCompletedAfter ??= _budget.Elapsed;
            _failureTreeQuiescenceBeganAfter ??= _budget.Elapsed;
            try
            {
                await WaitForTreeQuiescenceAsync(
                        useCleanupBudget: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.TreeQuiescence,
                    failure);
            }
            finally
            {
                _failureTreeQuiescenceCompletedAfter ??= _budget.Elapsed;
            }
        }
        else
        {
            try
            {
                _supervisor.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception failure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.Terminate,
                    failure);
            }
            _failureTerminationCompletedAfter ??= _budget.Elapsed;
        }

        _failureSupervisorReapBeganAfter ??= _budget.Elapsed;
        try
        {
            CloseOwnerLifetime(failures);
            await WaitForSupervisorExitAsync(
                    useCleanupBudget: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            AddCleanupFailure(
                failures,
                OwnedProcessCleanupStage.Reap,
                failure);
        }
        finally
        {
            _failureSupervisorReapCompletedAfter ??= _budget.Elapsed;
        }

        _failureStreamDrainBeganAfter ??= _budget.Elapsed;
        try
        {
            await WaitWithBudgetAsync(
                    Task.WhenAll(_standardOutput, _standardError),
                    _budget.RemainingCleanup,
                    "Owned process streams did not drain before the hard deadline.",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            AddCleanupFailure(
                failures,
                OwnedProcessCleanupStage.StreamDrain,
                failure);
        }
        finally
        {
            _failureStreamDrainCompletedAfter ??= _budget.Elapsed;
        }

        try
        {
            await WaitWithBudgetAsync(
                    _targetExitReport,
                    _budget.RemainingCleanup,
                    "The target-exit protocol did not close before the hard deadline.",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            AddCleanupFailure(
                failures,
                OwnedProcessCleanupStage.TargetExitProtocol,
                failure);
        }

        try
        {
            ReleaseResources();
        }
        catch (Exception failure)
        {
            AddCleanupFailure(
                failures,
                OwnedProcessCleanupStage.Dispose,
                failure);
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Owned process cleanup encountered multiple failures.", failures);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease must attempt every resource release and preserve all failures.")]
    private void ReleaseResources()
    {
        var failures = new Collection<Exception>();
        CloseOwnerLifetime(failures);
        foreach (var resource in new IDisposable?[]
                 {
                     _evidenceHold,
                     _targetExited,
                     _status,
                     _containment,
                     _supervisor
                 })
        {
            try
            {
                resource?.Dispose();
            }
            catch (Exception failure)
            {
                AddCleanupFailure(
                    failures,
                    OwnedProcessCleanupStage.Dispose,
                    failure);
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Owned process resource release failed.", failures);
        }
    }

    private void CloseOwnerLifetime(Collection<Exception>? failures = null)
    {
        if (Interlocked.Exchange(ref _ownerLifetimeClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _control.Dispose();
        }
        catch (Exception failure) when (failures != null)
        {
            AddCleanupFailure(
                failures,
                OwnedProcessCleanupStage.Dispose,
                failure);
        }
    }

    private Task FinalizeSupervisorAsync(CancellationToken cancellationToken)
    {
        return WriteWithBudgetAsync(
            _control,
            new[] { FinalizeSupervisor },
            _budget.RemainingOperation,
            "The supervisor finalization handoff exceeded the operation deadline.",
            cancellationToken);
    }

    private static async Task WriteFrameWithBudgetAsync(
        Stream stream,
        byte messageType,
        byte[] payload,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        var frame = new byte[checked(sizeof(byte) + sizeof(int) + payload.Length)];
        frame[0] = messageType;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(sizeof(byte)), payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(byte) + sizeof(int)));
        await WriteWithBudgetAsync(
                stream,
                frame,
                remaining,
                timeoutMessage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteWithBudgetAsync(
        Stream stream,
        byte[] payload,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    private static async Task<byte> ReadByteWithBudgetAsync(
        Stream stream,
        TransitionBudget budget,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var payload = await ReadExactWithBudgetAsync(
                stream,
                sizeof(byte),
                budget.RemainingOperation,
                timeoutMessage,
                cancellationToken)
            .ConfigureAwait(false);
        return payload[0];
    }

    private static Task<byte[]> ReadExactWithBudgetAsync(
        Stream stream,
        int length,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        return WaitWithBudgetAsync(
            ReadExactAsync(stream, length, cancellationToken),
            remaining,
            timeoutMessage,
            cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var payload = new byte[length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(
                    payload.AsMemory(offset, payload.Length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The process supervisor closed its status channel before completing the protocol.");
            }

            offset += read;
        }

        return payload;
    }

    private static async Task<byte[]?> ReadExactOrEofAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var payload = new byte[length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(
                    payload.AsMemory(offset, payload.Length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return null;
                }

                throw new EndOfStreamException(
                    "The process supervisor closed a partial target-exit report.");
            }

            offset += read;
        }

        return payload;
    }

    private static async Task<T> WaitWithBudgetAsync<T>(
        Task<T> operation,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            if (operation.IsCompleted)
            {
                return await operation.ConfigureAwait(false);
            }
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            return await operation.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    private static async Task WaitWithBudgetAsync(
        Task operation,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            if (operation.IsCompleted)
            {
                await operation.ConfigureAwait(false);
                return;
            }
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            await operation.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This helper returns cleanup failure so the caller can preserve it with the causal execution failure.")]
    private static async Task<Exception?> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    private static string ReadCompletedOutput(Task<string> output)
    {
        return output.Status == TaskStatus.RanToCompletion
            ? output.Result
            : string.Empty;
    }

    private static async Task<string> ReadToEndObservedAsync(
        StreamReader reader,
        ProcessStreamObservation observation)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                observation.MarkDrained();
                return output.ToString();
            }

            observation.MarkFirstObserved();
            output.Append(buffer, 0, read);
        }
    }

    private static IEnumerable<Exception> FlattenFailures(Exception failure)
    {
        return failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { failure };
    }

    private static void AddCleanupFailure(
        ICollection<Exception> failures,
        OwnedProcessCleanupStage stage,
        Exception failure)
    {
        foreach (var cause in FlattenFailures(failure))
        {
            failures.Add(new OwnedProcessCleanupStageException(stage, cause));
        }
    }

    private sealed record OwnershipAttachmentPayload(
        string ContainmentId,
        string MembershipId,
        string OwnerLifetimeId);

    private sealed record LaunchSpecPayload(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Environment,
        bool CloseStandardInput,
        EvidenceHoldTransport? EvidenceHold)
    {
        public static LaunchSpecPayload FromLaunchSpec(
            LaunchSpec launchSpec,
            EvidenceHoldCoordinator? evidenceHold)
        {
            var environment = new Dictionary<string, string?>(
                launchSpec.Environment,
                StringComparer.Ordinal);
            return new LaunchSpecPayload(
                launchSpec.FileName,
                launchSpec.Arguments,
                launchSpec.WorkingDirectory,
                environment,
                launchSpec.CloseStandardInput,
                evidenceHold?.Transport);
        }
    }

    private sealed record EvidenceHoldTransport(
        string TargetEnvironmentVariable,
        string CompletionClientHandle,
        string AcknowledgmentClientHandle);

    private sealed class EvidenceHoldCoordinator : IDisposable
    {
        private readonly object _sync = new();
        private readonly AnonymousPipeServerStream _completionPipe;
        private readonly AnonymousPipeServerStream _acknowledgmentPipe;
        private readonly byte _completionSignal;
        private readonly byte _acknowledgmentSignal;
        private readonly Task? _acknowledgmentPublicationGateForTesting;
        private readonly TaskCompletionSource<bool> _completionFinished = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _outcomeSnapshotStartedForTesting = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private EvidenceCaptureCompletion _captureCompletion;
        private bool _granted;
        private bool _completionStarted;
        private bool _outcomeSnapshotStarted;
        private bool _released;
        private bool _completionSignalDelivered;
        private bool _targetAcknowledged;
        private bool _localClientHandlesReleased;

        public EvidenceHoldCoordinator(
            EvidenceHoldRequest request,
            Task? acknowledgmentPublicationGateForTesting)
        {
            TargetEnvironmentVariable = request.TargetEnvironmentVariable;
            _completionSignal = request.CompletionSignal;
            _acknowledgmentSignal = request.AcknowledgmentSignal;
            _acknowledgmentPublicationGateForTesting =
                acknowledgmentPublicationGateForTesting;
            _completionPipe = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            _acknowledgmentPipe = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);
            Transport = new EvidenceHoldTransport(
                TargetEnvironmentVariable,
                _completionPipe.GetClientHandleAsString(),
                _acknowledgmentPipe.GetClientHandleAsString());
        }

        public string TargetEnvironmentVariable { get; }

        public EvidenceHoldTransport Transport { get; }

        public Task OutcomeSnapshotStartedForTesting =>
            _outcomeSnapshotStartedForTesting.Task;

        public EvidenceHoldOutcome Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return new EvidenceHoldOutcome(
                        Requested: true,
                        Granted: _granted,
                        CaptureCompletion: _captureCompletion,
                        Released: _released,
                        CompletionSignalDelivered: _completionSignalDelivered,
                        TargetAcknowledged: _targetAcknowledged);
                }
            }
        }

        public async Task<EvidenceHoldOutcome> SnapshotForOutcomeAsync(
            TransitionBudget budget,
            bool useCleanupBudget,
            CancellationToken cancellationToken)
        {
            Task? completion = null;
            lock (_sync)
            {
                _outcomeSnapshotStarted = true;
                if (_completionStarted)
                {
                    completion = _completionFinished.Task;
                }
            }
            _outcomeSnapshotStartedForTesting.TrySetResult(true);

            if (completion != null)
            {
                await WaitWithBudgetAsync(
                        completion,
                        useCleanupBudget
                            ? budget.RemainingCleanup
                            : budget.RemainingOperation,
                        useCleanupBudget
                            ? "The evidence-hold completion transaction did not settle before the hard deadline."
                            : "The evidence-hold completion transaction did not settle before the operation deadline.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Snapshot;
        }

        public void ReleaseLocalClientHandles()
        {
            lock (_sync)
            {
                if (_localClientHandlesReleased)
                {
                    return;
                }

                _completionPipe.DisposeLocalCopyOfClientHandle();
                _acknowledgmentPipe.DisposeLocalCopyOfClientHandle();
                _localClientHandlesReleased = true;
            }
        }

        public void Grant()
        {
            lock (_sync)
            {
                if (_released)
                {
                    throw new InvalidOperationException(
                        "The evidence hold was released before target launch completed.");
                }

                _granted = true;
            }
        }

        public async Task CompleteAsync(
            EvidenceCaptureCompletion completion,
            TransitionBudget budget,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_granted || _completionStarted || _outcomeSnapshotStarted || _released)
                {
                    throw new InvalidOperationException(
                        "The evidence hold is not awaiting capture completion.");
                }

                _completionStarted = true;
                _captureCompletion = completion;
            }

            try
            {
                await WriteWithBudgetAsync(
                        _completionPipe,
                        new[] { _completionSignal },
                        budget.RemainingOperation,
                        "The evidence-capture completion handoff exceeded the operation deadline.",
                        cancellationToken)
                    .ConfigureAwait(false);
                lock (_sync)
                {
                    _completionSignalDelivered = true;
                }

                var acknowledgment = await ReadExactWithBudgetAsync(
                        _acknowledgmentPipe,
                        sizeof(byte),
                        budget.RemainingOperation,
                        "The held target did not acknowledge capture completion before the operation deadline.",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (acknowledgment[0] != _acknowledgmentSignal)
                {
                    throw new InvalidDataException(
                        "The held target returned an invalid capture-completion acknowledgment.");
                }
                if (_acknowledgmentPublicationGateForTesting != null)
                {
                    await WaitWithBudgetAsync(
                            _acknowledgmentPublicationGateForTesting,
                            budget.RemainingOperation,
                            "The evidence-hold acknowledgment publication test gate exceeded the operation deadline.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                lock (_sync)
                {
                    _targetAcknowledged = true;
                }
            }
            finally
            {
                try
                {
                    Dispose();
                }
                finally
                {
                    _completionFinished.TrySetResult(true);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_released)
                {
                    return;
                }

                _released = true;
            }

            _completionPipe.Dispose();
            _acknowledgmentPipe.Dispose();
        }
    }

    private sealed record TargetExitReport(
        int ExitCode,
        long ExitedAtUnixMilliseconds,
        TimeSpan ExitedAfter);

    private sealed class ProcessStreamObservation(TransitionBudget budget)
    {
        private readonly object _gate = new();
        private TimeSpan? _firstObservedAfter;
        private TimeSpan? _drainedAfter;

        public TimeSpan? FirstObservedAfter
        {
            get
            {
                lock (_gate)
                {
                    return _firstObservedAfter;
                }
            }
        }

        public TimeSpan? DrainedAfter
        {
            get
            {
                lock (_gate)
                {
                    return _drainedAfter;
                }
            }
        }

        public void MarkFirstObserved()
        {
            lock (_gate)
            {
                _firstObservedAfter ??= budget.Elapsed;
            }
        }

        public void MarkDrained()
        {
            lock (_gate)
            {
                _drainedAfter ??= budget.Elapsed;
            }
        }
    }
}
