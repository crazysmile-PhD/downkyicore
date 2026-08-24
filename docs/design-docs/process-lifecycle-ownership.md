# Process Lifecycle Ownership

## Problem Statement

The historical process, restart and lifecycle failure family has one common
root cause: PID and PPID observation have been used in place of launch-time OS
process ownership.

PID, PPID, process start time, WMI and `ps` process-tree data are useful
diagnostics. They must not be an ownership authority, kill-target authority,
residual-process correctness oracle, reap authority or exact process identity.
The final correctness truth must come from OS-backed ownership state established
before target code executes.

## Architecture Boundaries

```text
CentralTestRunner
  -> OwnedProcessLease

LifecyclePhaseSupervisor
  -> OwnedProcessLease
  -> ForensicsObserver

RestartTransaction
  -> OwnedProcessLease
  -> ParentLifetimeLease
```

`OwnedProcessLease` is not a generic domain supervisor. It owns only:

- an immutable `LaunchSpec`;
- launch and pre-execution ownership establishment;
- stable root identity and owned-tree containment;
- supervisor/control handles and standard-I/O endpoints;
- an explicit inherited-handle allowlist;
- consumption of a caller-owned `TransitionBudget`;
- bounded wait, termination, tree-quiescence, reap and stream drain;
- a typed process outcome and diagnostic snapshot.

It does not know restart authorization semantics, test selection, test-platform
policy, TRX interpretation, lifecycle phase semantics, forensics interpretation,
release policy or the policy value of any timeout.

`ParentLifetimeLease` is separate because observing the process that created a
restart helper is not the same operation as owning a child that this process
launched. The two contracts must not claim identical platform capabilities.

## Threat Model

The current threat model is trusted repository-child bug containment. The
supervisor must contain accidentally retained children and subprocesses, early
parent exit, inherited handles, teardown bugs, ordinary daemon-like behavior and
stdout/stderr closure bugs.

The current contract does not claim containment of a hostile child that calls
`setsid()`, deliberately escapes its process group or attempts a sandbox escape.
Windows Job Objects and pre-exec Linux/macOS process groups or sessions are
sufficient for this threat model. Do not add cgroups, privileged daemons or
system services unless a future requirement explicitly introduces untrusted
executables and a separate security/sandbox boundary.

## Restart Product Policy

Restart retains existing Policy B:

```text
cleanup failure
  -> preserve the failure
  -> still attempt desktop termination handoff
  -> commit restart when the desktop handoff is accepted

desktop handoff failure
  -> do not commit
  -> revoke the prepared helper
```

Cleanup failure must not be reinterpreted as unconditional `No Relaunch`. This
is existing product behavior and is not owned by process-supervision plumbing.

## Restart Guarantees

Restart provides a strong safety guarantee. If authorization, stable parent
identity, exact parent exit or a required transition cannot be proved, the
helper must not relaunch.

Availability is best effort. Commit does not guarantee a replacement process.
A helper crash, parent hang or relaunch-start failure may fail closed and lose
restart availability. A future requirement for guaranteed restart after commit
would require an external persistent OS or service supervisor and is outside
`OwnedProcessLease`.

## Platform Semantics

### Windows

- A stable process `HANDLE` identifies the root.
- The target must join its Job Object before target code executes.
- `Process.Start` followed by `AssignProcessToJobObject` is not atomic ownership.
- A backend may use `CREATE_SUSPENDED -> assign Job -> resume`, or an inert
  launch host that cannot start target code until Job assignment is confirmed.
- Assignment failure must terminate the still-inert child and fail closed.
- Kill-on-close and bounded Job termination close the owned tree.

### Linux

- Prefer pidfd when available for stable root identity; pidfd does not contain
  descendants.
- Establish a process group or session before target `exec` for descendant
  ownership.
- A minimal native shim or interoperability layer is allowed when public .NET
  APIs cannot establish this boundary.
- Implementation difficulty never authorizes a PID/PPID correctness fallback.

### macOS

- macOS has no pidfd.
- Direct-child wait/reap is authoritative for an owner-created root.
- A process group or session provides trusted-child descendant containment.
- A retained lifetime capability or pipe can represent restart-parent lifetime.
- `kqueue` `EVFILT_PROC` / `NOTE_EXIT` is kernel observation, not ownership.

Reports must disclose the actual platform identity and containment strength.
The abstraction must not claim stronger or identical semantics where the OS
does not provide them.

## Handle And Stream Lifecycle Closure

Process ownership alone does not establish lifecycle closure. The same owner
must account for process state, handles or file descriptors, stdin, stdout,
stderr and bounded stream drain.

Stdout and stderr drain concurrently from launch. They must not begin only after
root exit. If a descendant retains an output handle, owned-tree termination must
close the handle before the owner completes a bounded drain; waiting indefinitely
for EOF is forbidden.

Authorization, diagnostic and lifetime pipes retain their domain semantics.
The low-level lease owns their declared handle lifetime and permits inheritance
only through an immutable explicit allowlist.

## Deadline Model

Every operation receives one `TransitionBudget` based on an absolute monotonic
clock. Nested operations may read remaining time but cannot extend the deadline
or create a substitute deadline.

Wait, evidence capture, termination, reap and stream drain consume that budget.
The budget may reserve bounded cleanup time after the operation cutoff, but the
operation and hard-cleanup deadlines are established together by the same owner
on one monotonic timeline. Cleanup must not introduce a second clock owner.

Caller cancellation may stop work before an irreversible transition. Once a
child has started or cleanup has begun, cleanup is not abandoned by caller
cancellation and remains bounded by the hard deadline.

## Forensics Boundary

`LifecyclePhaseSupervisor` owns process truth. `ForensicsObserver` consumes
snapshots and cannot kill a process, release a child, extend child lifetime,
create process ownership or extend the transition deadline.

A deterministic fixture may request this supervisor-owned sub-state:

```text
Running
  -> EvidenceHoldGranted
  -> EvidenceCaptured | EvidenceFailed
  -> Released
```

The hold is part of the supervisor state machine and the same transition budget.
Forensics failure may fail the phase but cannot prevent bounded cleanup.

## Legacy Mechanism Disposition

The completed migration removes these mechanisms from correctness paths:

- `Get-ProcessTree` WMI/`ps` PPID recursion;
- `Get-ProcessIdentityKey` and `Get-LiveObservedProcess` as authorities;
- PID plus start time as restart authority;
- `Wait-ResidualProcessTree`;
- synthetic-only `ReleaseObservedChildren` correctness;
- `New/Start/Complete/Close-ObservedChildReleaseLease` as a second owner;
- duplicate start, wait, kill and reap implementations;
- independent timeout owners for the same transition.

The migration retains central-runner capability authorization, canonical test
arguments, TRX semantic validation, desktop cleanup/handoff failure aggregation
and PID/PPID/thread/stack diagnostics. Diagnostic identity must not affect
success, kill target, reap target or residual classification.
