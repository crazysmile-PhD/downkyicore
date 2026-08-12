# Unexpected Download Cancellation Recovery

Status: v1.1.1 release-health candidate; revalidate on the integration branch

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

## Checkpoint

A local uncommitted prototype was started on
`fix/unexpected-cancellation-recovery`, but it is not authoritative and must not
be copied mechanically. Reconcile the invariant against the final v1.1.1
integration architecture, search sibling cancellation paths, and derive the
transition matrix before retaining any implementation.

## Acceptance

The pre-fix deterministic regression must demonstrate the stuck-Downloading
failure, then pass on the focused fix. Strict Release, all affected tests,
review invariants, architecture and lifecycle gates must pass on the final
integration exact head.

## Rollback

Revert the focused cancellation commit and its tests together. Do not replace
typed cancellation semantics with a broad catch or generic failure sentinel.
