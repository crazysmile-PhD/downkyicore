using DownKyi.Domain.Results;

namespace DownKyi.Domain.Downloads;

public sealed class DownloadTask
{
    private DownloadTask(
        DownloadTaskId id,
        DownloadTaskMetadata metadata,
        DownloadPlan plan,
        DownloadOutput output,
        DownloadPhase phase,
        DownloadProgress progress,
        DownloadTransferState transfer,
        DownloadFailure? failure,
        DownloadCompletion? completion,
        long version,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        Id = id;
        Metadata = metadata;
        Plan = plan;
        Output = output;
        Phase = phase;
        Progress = progress;
        Transfer = transfer;
        Failure = failure;
        Completion = completion;
        Version = version;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public DownloadTaskId Id { get; }

    public DownloadTaskMetadata Metadata { get; }

    public DownloadPlan Plan { get; }

    public DownloadOutput Output { get; }

    public DownloadPhase Phase { get; }

    public DownloadProgress Progress { get; }

    public DownloadTransferState Transfer { get; }

    public DownloadFailure? Failure { get; }

    public DownloadCompletion? Completion { get; }

    public long Version { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static DownloadTask Create(
        DownloadTaskId id,
        DownloadTaskMetadata metadata,
        DownloadPlan plan,
        DownloadOutput output,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        return new DownloadTask(
            id,
            metadata,
            plan,
            output,
            DownloadPhase.Queued,
            DownloadProgress.None,
            DownloadTransferState.Empty,
            null,
            null,
            0,
            createdAtUtc,
            createdAtUtc);
    }

    public static DownloadTask Restore(
        DownloadTaskId id,
        DownloadTaskMetadata metadata,
        DownloadPlan plan,
        DownloadOutput output,
        DownloadPhase phase,
        DownloadProgress progress,
        DownloadTransferState transfer,
        DownloadFailure? failure,
        DownloadCompletion? completion,
        long version,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentOutOfRangeException.ThrowIfLessThan(updatedAtUtc, createdAtUtc);

        ValidatePhasePayload(phase, failure, completion);
        return new DownloadTask(
            id,
            metadata,
            plan,
            output,
            phase,
            progress,
            transfer,
            failure,
            completion,
            version,
            createdAtUtc,
            updatedAtUtc);
    }

    public OperationResult<DownloadTask> Start(DateTimeOffset now)
    {
        return TransitionTo(DownloadPhase.Downloading, now);
    }

    public OperationResult<DownloadTask> Pause(DateTimeOffset now)
    {
        return Phase switch
        {
            DownloadPhase.Queued => TransitionTo(DownloadPhase.Paused, now),
            DownloadPhase.Downloading => TransitionTo(DownloadPhase.Pausing, now),
            _ => InvalidTransition(DownloadPhase.Pausing)
        };
    }

    public OperationResult<DownloadTask> ConfirmPaused(DateTimeOffset now)
    {
        return TransitionTo(DownloadPhase.Paused, now);
    }

    public OperationResult<DownloadTask> Resume(DateTimeOffset now)
    {
        return TransitionTo(DownloadPhase.Queued, now);
    }

    public OperationResult<DownloadTask> Retry(DateTimeOffset now)
    {
        return Phase == DownloadPhase.Failed
            ? TransitionTo(DownloadPhase.Queued, now)
            : InvalidTransition(DownloadPhase.Queued);
    }

    public OperationResult<DownloadTask> Cancel(DateTimeOffset now)
    {
        return TransitionTo(DownloadPhase.Canceled, now);
    }

    public OperationResult<DownloadTask> Delete(DateTimeOffset now)
    {
        return TransitionTo(DownloadPhase.Deleted, now);
    }

    public OperationResult<DownloadTask> Fail(DownloadFailure failure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionTo(DownloadPhase.Failed, now, failure: failure);
    }

    public OperationResult<DownloadTask> Complete(DownloadCompletion completion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return TransitionTo(DownloadPhase.Completed, now, completion: completion);
    }

    public OperationResult<DownloadTask> UpdateProgress(DownloadProgress progress, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Phase is not (DownloadPhase.Downloading or DownloadPhase.Pausing))
        {
            return InvalidTransition(Phase);
        }

        EnsureTimestampDoesNotMoveBackward(now);
        return OperationResult.Success(new DownloadTask(
            Id,
            Metadata,
            Plan,
            Output,
            Phase,
            progress,
            Transfer,
            Failure,
            Completion,
            checked(Version + 1),
            CreatedAtUtc,
            now));
    }

    public OperationResult<DownloadTask> UpdateTransferState(DownloadTransferState transfer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Deleted)
        {
            return OperationResult.Failure<DownloadTask>(new OperationError(
                "download.transfer.terminal",
                $"Cannot update transfer state after a download reaches {Phase}.",
                OperationErrorKind.Conflict));
        }

        EnsureTimestampDoesNotMoveBackward(now);
        return OperationResult.Success(new DownloadTask(
            Id,
            Metadata,
            Plan,
            Output,
            Phase,
            Progress,
            transfer,
            Failure,
            Completion,
            checked(Version + 1),
            CreatedAtUtc,
            now));
    }

    private OperationResult<DownloadTask> TransitionTo(
        DownloadPhase target,
        DateTimeOffset now,
        DownloadFailure? failure = null,
        DownloadCompletion? completion = null)
    {
        if (!CanTransition(Phase, target))
        {
            return InvalidTransition(target);
        }

        EnsureTimestampDoesNotMoveBackward(now);
        ValidatePhasePayload(target, failure, completion);
        return OperationResult.Success(new DownloadTask(
            Id,
            Metadata,
            Plan,
            Output,
            target,
            Progress,
            Transfer,
            failure,
            completion,
            checked(Version + 1),
            CreatedAtUtc,
            now));
    }

    private OperationResult<DownloadTask> InvalidTransition(DownloadPhase target)
    {
        return OperationResult.Failure<DownloadTask>(new OperationError(
            "download.transition.invalid",
            $"Cannot transition a download from {Phase} to {target}.",
            OperationErrorKind.Conflict));
    }

    private static bool CanTransition(DownloadPhase source, DownloadPhase target)
    {
        return source switch
        {
            DownloadPhase.Queued => target is DownloadPhase.Downloading or DownloadPhase.Paused
                or DownloadPhase.Failed or DownloadPhase.Canceled or DownloadPhase.Deleted,
            DownloadPhase.Downloading => target is DownloadPhase.Pausing or DownloadPhase.Completed
                or DownloadPhase.Failed or DownloadPhase.Canceled or DownloadPhase.Deleted,
            DownloadPhase.Pausing => target is DownloadPhase.Paused or DownloadPhase.Queued
                or DownloadPhase.Failed or DownloadPhase.Canceled or DownloadPhase.Deleted,
            DownloadPhase.Paused => target is DownloadPhase.Queued or DownloadPhase.Failed
                or DownloadPhase.Canceled or DownloadPhase.Deleted,
            DownloadPhase.Failed => target is DownloadPhase.Queued or DownloadPhase.Canceled
                or DownloadPhase.Deleted,
            DownloadPhase.Canceled => target is DownloadPhase.Deleted,
            DownloadPhase.Completed => target is DownloadPhase.Deleted,
            DownloadPhase.Deleted => false,
            _ => false
        };
    }

    private static void ValidatePhasePayload(
        DownloadPhase phase,
        DownloadFailure? failure,
        DownloadCompletion? completion)
    {
        if ((phase == DownloadPhase.Failed) != (failure != null))
        {
            throw new ArgumentException("Failure details must exist only for failed downloads.", nameof(failure));
        }

        if ((phase == DownloadPhase.Completed) != (completion != null))
        {
            throw new ArgumentException("Completion details must exist only for completed downloads.", nameof(completion));
        }
    }

    private void EnsureTimestampDoesNotMoveBackward(DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(now, UpdatedAtUtc);
    }
}
