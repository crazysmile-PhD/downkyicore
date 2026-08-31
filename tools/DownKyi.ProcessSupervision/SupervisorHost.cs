using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace DownKyi.ProcessSupervision;

internal interface ISupervisorHostCapability
{
    ValueTask<ProcessOwnershipMetadata> AttachOwnershipAsync(
        ContainmentAttachment attachment,
        CancellationToken cancellationToken);

    ValueTask<TargetStarted> AuthorizeLaunchAsync(
        LaunchSpec launchSpec,
        CancellationToken cancellationToken);

    ValueTask<TargetExited> WaitForTargetExitAsync(CancellationToken cancellationToken);

    ValueTask FinalizeAsync(CancellationToken cancellationToken);

    ValueTask FailSafeOwnerLossAsync();
}

internal enum SupervisorHostCompletionKind
{
    Finalized,
    OwnerChannelClosed,
    ProtocolRejected
}

internal sealed record SupervisorHostResult(
    SupervisorHostCompletionKind Kind,
    SupervisorProtocolError? ProtocolError = null,
    Exception? FailSafeFailure = null);

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal carrier preserves both execution and fail-safe evidence.")]
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "This exception is an internal seam for the internal supervisor host.")]
internal sealed class SupervisorHostExecutionException : Exception
{
    internal SupervisorHostExecutionException(
        Exception executionFailure,
        Exception failSafeFailure)
        : base("Supervisor host execution and fail-safe cleanup both failed.", executionFailure)
    {
        ExecutionFailure = executionFailure;
        FailSafeFailure = failSafeFailure;
    }

    internal Exception ExecutionFailure { get; }

    internal Exception FailSafeFailure { get; }
}

internal static class SupervisorHost
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This boundary must retain execution evidence while the fail-safe is attempted exactly once.")]
    internal static async Task<SupervisorHostResult> RunAsync(
        Stream ownerCommands,
        Stream ownerStatus,
        ISupervisorHostCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerCommands);
        ArgumentNullException.ThrowIfNull(ownerStatus);
        ArgumentNullException.ThrowIfNull(capability);

        var state = new SupervisorProtocolState();
        var attachmentRead = await ReadExpectedAsync<AttachOwnershipFrame>(
                ownerCommands,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        if (attachmentRead.Result != null)
        {
            return attachmentRead.Result;
        }

        var readiness = await capability.AttachOwnershipAsync(
                attachmentRead.Frame!.Attachment,
                cancellationToken)
            .ConfigureAwait(false);
        var readinessResult = await WriteAsync(
                ownerStatus,
                state,
                new OwnershipReadyFrame(readiness),
                cancellationToken)
            .ConfigureAwait(false);
        if (readinessResult != null)
        {
            return readinessResult;
        }

        var authorizationRead = await ReadExpectedAsync<AuthorizeLaunchFrame>(
                ownerCommands,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorizationRead.Result != null)
        {
            return authorizationRead.Result;
        }

        SupervisorHostResult executionResult;
        try
        {
            executionResult = await RunAuthorizedAsync(
                    ownerCommands,
                    ownerStatus,
                    capability,
                    state,
                    authorizationRead.Frame!.LaunchSpec,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception executionFailure)
        {
            var failSafeFailure = await TryFailSafeOwnerLossAsync(capability)
                .ConfigureAwait(false);
            if (failSafeFailure != null)
            {
                throw new SupervisorHostExecutionException(executionFailure, failSafeFailure);
            }

            ExceptionDispatchInfo.Capture(executionFailure).Throw();
            throw new UnreachableException();
        }

        if (executionResult.Kind == SupervisorHostCompletionKind.Finalized)
        {
            return executionResult;
        }

        var supplementalFailure = await TryFailSafeOwnerLossAsync(capability)
            .ConfigureAwait(false);
        return supplementalFailure == null
            ? executionResult
            : executionResult with { FailSafeFailure = supplementalFailure };
    }

    private static async Task<SupervisorHostResult> RunAuthorizedAsync(
        Stream ownerCommands,
        Stream ownerStatus,
        ISupervisorHostCapability capability,
        SupervisorProtocolState state,
        LaunchSpec launchSpec,
        CancellationToken cancellationToken)
    {
        var targetStarted = await capability.AuthorizeLaunchAsync(
                launchSpec,
                cancellationToken)
            .ConfigureAwait(false);
        var targetStartedResult = await WriteAsync(
                ownerStatus,
                state,
                new TargetStartedFrame(targetStarted),
                cancellationToken)
            .ConfigureAwait(false);
        if (targetStartedResult != null)
        {
            return targetStartedResult;
        }

        var targetExitTask = capability.WaitForTargetExitAsync(cancellationToken).AsTask();
        var ownerSignalTask = SupervisorProtocolCodec.ReadAsync(
                ownerCommands,
                cancellationToken)
            .AsTask();
        _ = await Task.WhenAny(targetExitTask, ownerSignalTask)
            .ConfigureAwait(false);
        if (ownerSignalTask.IsCompleted)
        {
            var earlyOwnerSignal = await ownerSignalTask.ConfigureAwait(false);
            if (earlyOwnerSignal is SupervisorProtocolChannelClosed)
            {
                return new SupervisorHostResult(SupervisorHostCompletionKind.OwnerChannelClosed);
            }
            if (earlyOwnerSignal is SupervisorProtocolReadRejected rejected)
            {
                return Reject(rejected.Error);
            }

            var earlyFrame = ((SupervisorProtocolFrameRead)earlyOwnerSignal).Frame;
            return Reject(new SupervisorProtocolError(
                SupervisorProtocolErrorKind.UnexpectedFrame,
                "The owner emitted a supervision frame before target exit was published.",
                SupervisorProtocolKind.TargetExited,
                earlyFrame.Kind));
        }

        var targetExited = await targetExitTask.ConfigureAwait(false);
        var targetExitedResult = await WriteAsync(
                ownerStatus,
                state,
                new TargetExitedFrame(targetExited),
                cancellationToken)
            .ConfigureAwait(false);
        if (targetExitedResult != null)
        {
            return targetExitedResult;
        }

        var finalizeRead = await ownerSignalTask.ConfigureAwait(false);
        var finalize = AcceptExpected<FinalizeFrame>(finalizeRead, state);
        if (finalize.Result != null)
        {
            return finalize.Result;
        }

        await capability.FinalizeAsync(cancellationToken).ConfigureAwait(false);
        var finalizedResult = await WriteAsync(
                ownerStatus,
                state,
                new FinalizedFrame(),
                cancellationToken)
            .ConfigureAwait(false);
        if (finalizedResult != null)
        {
            return finalizedResult;
        }

        if (!state.IsComplete)
        {
            return Reject(new SupervisorProtocolError(
                SupervisorProtocolErrorKind.UnexpectedFrame,
                "The supervision protocol did not reach its final state."));
        }

        return new SupervisorHostResult(SupervisorHostCompletionKind.Finalized);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The host must preserve fail-safe evidence without replacing execution evidence.")]
    private static async ValueTask<Exception?> TryFailSafeOwnerLossAsync(
        ISupervisorHostCapability capability)
    {
        try
        {
            await capability.FailSafeOwnerLossAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception failSafeFailure)
        {
            return failSafeFailure;
        }
    }

    private static async ValueTask<ExpectedFrameResult<TFrame>> ReadExpectedAsync<TFrame>(
        Stream ownerCommands,
        SupervisorProtocolState state,
        CancellationToken cancellationToken)
        where TFrame : SupervisorProtocolFrame
    {
        var read = await SupervisorProtocolCodec.ReadAsync(ownerCommands, cancellationToken)
            .ConfigureAwait(false);
        return AcceptExpected<TFrame>(read, state);
    }

    private static ExpectedFrameResult<TFrame> AcceptExpected<TFrame>(
        SupervisorProtocolReadResult read,
        SupervisorProtocolState state)
        where TFrame : SupervisorProtocolFrame
    {
        if (read is SupervisorProtocolChannelClosed)
        {
            return new ExpectedFrameResult<TFrame>(
                null,
                new SupervisorHostResult(SupervisorHostCompletionKind.OwnerChannelClosed));
        }
        if (read is SupervisorProtocolReadRejected rejected)
        {
            return new ExpectedFrameResult<TFrame>(null, Reject(rejected.Error));
        }
        if (read is not SupervisorProtocolFrameRead { Frame: TFrame frame })
        {
            var actualKind = ((SupervisorProtocolFrameRead)read).Frame.Kind;
            return new ExpectedFrameResult<TFrame>(
                null,
                Reject(new SupervisorProtocolError(
                    SupervisorProtocolErrorKind.UnexpectedFrame,
                    "The supervision frame type is illegal in the current protocol state.",
                    ExpectedKindFor<TFrame>(),
                    actualKind)));
        }

        var stateError = state.Advance(frame.Kind);
        return stateError == null
            ? new ExpectedFrameResult<TFrame>(frame, null)
            : new ExpectedFrameResult<TFrame>(null, Reject(stateError));
    }

    private static async ValueTask<SupervisorHostResult?> WriteAsync(
        Stream ownerStatus,
        SupervisorProtocolState state,
        SupervisorProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        var stateError = state.Validate(frame.Kind);
        if (stateError != null)
        {
            return Reject(stateError);
        }

        var writeError = await SupervisorProtocolCodec.WriteAsync(
                ownerStatus,
                frame,
                cancellationToken)
            .ConfigureAwait(false);
        if (writeError != null)
        {
            return Reject(writeError);
        }

        var advanceError = state.Advance(frame.Kind);
        return advanceError == null ? null : Reject(advanceError);
    }

    private static SupervisorProtocolKind ExpectedKindFor<TFrame>()
        where TFrame : SupervisorProtocolFrame
    {
        return typeof(TFrame) == typeof(AttachOwnershipFrame)
            ? SupervisorProtocolKind.AttachOwnership
            : typeof(TFrame) == typeof(AuthorizeLaunchFrame)
                ? SupervisorProtocolKind.AuthorizeLaunch
                : SupervisorProtocolKind.Finalize;
    }

    private static SupervisorHostResult Reject(SupervisorProtocolError error)
    {
        return new SupervisorHostResult(SupervisorHostCompletionKind.ProtocolRejected, error);
    }

    private sealed record ExpectedFrameResult<TFrame>(
        TFrame? Frame,
        SupervisorHostResult? Result)
        where TFrame : SupervisorProtocolFrame;
}
