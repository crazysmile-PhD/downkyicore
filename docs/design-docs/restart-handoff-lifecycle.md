# Restart Handoff Lifecycle

## Status

Stage 4A is a feasibility checkpoint. It records an invalidated composition
assumption and defines the executable proof required before any production
restart migration. It does not authorize or complete Stage 4 implementation.

## Confirmed Conflict

The original Stage 4 plan treated a committed restart helper as an ordinary
`OwnedProcessLease` child. Exact-head evidence proves that composition is
invalid:

1. `OwnedProcessLeasePlatformTests.OwnerLifetimeClosureTriggersBoundedOwnedTreeCleanup`
   closes the owner-lifetime channel while the owned target is still running and
   requires bounded terminate, quiescence and reap. The companion
   `OwnerDeathAfterTargetExitStillTerminatesRetainedDescendants` proves the same
   invariant after the root exits while descendants remain.
2. `ProcessRestartLauncherTests` and `AvaloniaApplicationLifecycleTests` preserve
   the restart product transaction: prepare a helper, commit only after desktop
   termination handoff is accepted, let the helper survive the exact old desktop
   exit, launch one replacement, then terminate the helper.
3. `OwnedProcessLease` exposes launch, wait and disposal. Its supervisor accepts
   finalization only after target exit and authoritative tree closure. It has no
   transfer, detach or committed-successor transition that can outlive owner EOF
   without disabling the Stage 2 owner-death cleanup invariant.

Therefore:

> Stage 4 original composition assumption invalidated.

This is not a Stage 2 defect. An ordinary owned child and a committed restart
successor belong to different lifetime domains.

## Domain Boundary

```text
ordinary owned child
  owner lifetime EOF
    -> bounded terminate
    -> authoritative quiescence
    -> reap and stream drain

restart handoff candidate
  Prepared
    -> ExactParentWatcherArmed
    -> Ready
    -> Authorized
    -> Committed
    -> ExactParentExited
    -> RelaunchAttemptedExactlyOnce
    -> TerminalExit

  any pre-commit failure
    -> RevokedOrRejected
    -> NoRelaunch
    -> TerminalExit
```

The post-commit helper is a bounded transaction successor. It is not a detached
daemon or persistent service. Its only authority is immutable one-shot restart
authorization, its exact-parent watcher and the remaining portion of the single
handoff deadline. It cannot acquire a new timeout, accept a second commit or
remain alive after a terminal outcome.

## Candidate Platform Contracts

These are feasibility candidates, not accepted production backends. Each must
be exercised natively before Stage 4 may resume.

### Windows

Acquire a process `HANDLE` while the old desktop is alive, retain only the
rights required to wait, and report `Ready` only after the exact process object
can be waited. The handle, not the numeric PID or start time, is correctness
authority. Process exit signals that retained object even if the numeric PID is
later reused.

### Linux

Call `pidfd_open` while the old desktop is alive and wait for readiness with
`poll` or `epoll`. The pidfd, not PID, PPID, `/proc` enumeration or start time,
is correctness authority. Unsupported pidfd behavior is a capability failure;
there is no polling fallback.

### macOS

Register `EVFILT_PROC` plus `NOTE_EXIT` in a `kqueue` while the old desktop is
alive. Return `Ready` only after the registration succeeds, and do not permit
the desktop to finish handoff before that readiness. PID observation is only the
input used to arm the kernel watcher; it is not a later polling authority.

## Deadline Handoff Candidate

Prepare fixes one immutable absolute deadline in a platform monotonic-clock
domain. The helper receives the clock-domain identifier, absolute expiry and any
required conversion metadata as authenticated handoff data. It may read that
same clock only to calculate remaining time. It cannot restart a stopwatch,
renew the product window or create an independent `WaitAsync` window.

The feasibility harness must prove that time consumed by the old desktop reduces
the helper's remaining window and that a fresh-clock mutation is rejected.
Serialization details remain a feasibility result, not a production API choice.

## Contract Responsibilities

`ParentLifetimeLease` answers only whether the exact original parent exited. It
does not own or terminate the helper, authorize restart, commit a transaction or
create a deadline. If native feasibility succeeds, a separate restart domain,
conceptually `RestartHandoffLease` coordinated by `RestartCoordinator`, composes
that observation with typed authorization, one immutable deadline and bounded
terminal helper behavior.

`OwnedProcessLease` remains unchanged and continues to own ordinary launched
children. No `IgnoreOwnerDeathAfterCommit` flag or restart-specific weakening is
permitted.

## Acceptance Boundary

Stage 4A must behaviorally prove exact watcher arming before handoff, stale and
reused identity rejection, authorization EOF/partial/replay rejection,
pre-commit parent exit, committed parent exit, parent hang, helper crash,
deadline exhaustion, relaunch failure, exactly-once relaunch and terminal helper
closure. Mutations must reject a fresh helper clock, late watcher registration,
numeric-PID authority and ordinary owner-death ownership across the committed
successor transition.

Source-shape checks are supplemental only. Missing native capability, a second
deadline owner, PID/PPID correctness, weakened Stage 2 cleanup, an unbounded
helper or a persistent service triggers the Stage 4A stop rule.
