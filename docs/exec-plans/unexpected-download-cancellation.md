# Unexpected Download Cancellation Recovery

Status: integrated v1.1.1 release invariant

## Invariant

An `OperationCanceledException` is a normal pause, shutdown or user-cancel
outcome only when the operation's owning token is canceled or the durable task
has intentionally left its active state. A timeout or unrelated cancellation
while the owning token remains active must not be swallowed and leave the task
durably `Downloading`.

## Required Transition Proof

- pause, shutdown and user cancellation preserve their current semantics;
- a stage that observes the owning token canceled exits without marking a
  failure;
- a task that intentionally leaves its active state stops the stage sequence
  without finalization;
- `TaskCanceledException` while the owning token is active becomes an observed,
  retryable failed task rather than a stuck active task;
- aria2 and builtin backends follow the same contract;
- the orchestrator does not confuse an expected stop signal with an unexpected
  transport cancellation.

## Implementation

- `DownloadPipeline` catches cancellation only when its owning token is canceled.
- `DownloadOrchestrator` distinguishes shutdown, per-task cancellation, and an
  unexpected cancellation while the execution token remains active.
- Unexpected cancellation calls the existing retryable failure owner with an
  uncanceled persistence token. The durable-phase guard prevents mutation after
  pause, cancel, completion, or deletion.
- The worker continues after recording the failure, so one transport timeout
  cannot reduce the fixed worker pool.

## Acceptance

The deterministic regression demonstrates the old stuck-Downloading failure and
proves the fixed worker records Failed before executing the next queued task.
Strict Release and the formal exact-head gates remain release requirements.

## Rollback

Revert the focused cancellation commit and its tests together. Do not replace
typed cancellation semantics with a broad catch or generic failure sentinel.
