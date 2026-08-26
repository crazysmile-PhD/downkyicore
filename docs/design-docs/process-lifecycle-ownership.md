# Process Lifecycle Ownership

## Problem Statement

The historical process, restart and lifecycle failure family has one common
root cause: PID and PPID observation have been used in place of launch-time OS
process ownership.

PID, PPID, process start time, WMI and `ps` process-tree data are useful
diagnostics. They must not be an ownership authority, kill-target authority,
residual-process correctness oracle, reap authority or exact process identity.
The final correctness truth must come from OS-backed ownership state established
before target code executes. Stable identity, descendant containment and
membership/quiescence are separate contracts. A primitive that supplies one of
them must not be treated as proof of the others.

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
- stable root or group-anchor identity, owned-tree containment and an explicit
  membership/quiescence authority;
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
Trusted-child does not mean cooperative lifetime-lease propagation: an ordinary
child may launch a descendant through a runtime API that closes unlisted file
descriptors. Process groups are therefore useful containment and termination
primitives, but are not by themselves a stable identity or a descendant
membership/quiescence authority.

Privileged daemons or system services remain outside this threat model. An
OS-backed membership primitive such as a delegated cgroup is permitted when it
is required to prove lifecycle closure for trusted children; inability to
establish the selected backend fails closed before target authorization.

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

### Authority Separation

Every backend must identify one primitive for each row below. One primitive may
implement multiple rows only where the OS contract actually provides them.

| Authority | Required answer |
| --- | --- |
| Stable identity | Is this still the exact root or supervisor-owned anchor established at launch? |
| Containment | Which descendants receive bounded termination as one owned set? |
| Membership/quiescence | Does that exact owned set contain any member that can still execute, fork or retain an owned endpoint? |
| Reap | Which direct children must this supervisor collect? |

PID, PPID and numeric PGID enumeration cannot fill any missing row. They remain
diagnostic evidence only.

### Windows

- A stable process `HANDLE` identifies the root.
- The target must join its Job Object before target code executes.
- `Process.Start` followed by `AssignProcessToJobObject` is not atomic ownership.
- A backend may use `CREATE_SUSPENDED -> assign Job -> resume`, or an inert
  launch host that cannot start target code until Job assignment is confirmed.
- Assignment failure must terminate the still-inert child and fail closed.
- Kill-on-close and bounded Job termination close the owned tree.

### Linux

- Prefer pidfd for stable anchor identity. A pidfd identifies one task; it does
  not prove descendant membership. `PIDFD_SIGNAL_PROCESS_GROUP`, where
  available, supplies stable group signalling while the anchor exists, not a
  membership oracle.
- Establish a process group before target `exec`, with a supervisor-owned anchor
  that remains alive through all destructive group operations. The group is a
  containment/termination primitive only.
- A delegated cgroup v2 child is the current preferred membership candidate.
  Recursive
  `cgroup.events` `populated` state proves that no live process remains, and
  `cgroup.kill` provides atomic tree termination against concurrent forks and
  migration within the delegated subtree. Direct-child zombies remain the
  separate reap authority's responsibility.
- The backend must prove delegation and required files before authorizing target
  execution. A machine without that capability is unsupported for the formal
  lifecycle gate until another stable membership backend is designed and
  behaviorally proved.
- A minimal native shim or interoperability layer is allowed when public .NET
  APIs cannot establish this boundary. Implementation difficulty never
  authorizes a PID/PPID/PGID polling fallback.

### macOS

- macOS has no pidfd.
- A retained direct-child handle and wait/reap state identify the
  supervisor-owned group anchor. The anchor must not be reaped before group
  termination is complete and membership quiescence has been proved.
- A process group provides trusted-child descendant containment and termination
  while the anchor identity is retained. Numeric PGID probing is not a
  membership/quiescence authority.
- `proc_listpgrppids` is the current candidate for an atomic kernel membership
  snapshot of the anchored group. It must exclude the intentionally live anchor
  and prove zero remaining members before the anchor is reaped. The API is a
  private libproc interface and remains provisional until native x64 and arm64
  behavioral tests prove its availability, zombie semantics, buffer/error
  contract and reparent behavior.
- `kqueue` `EVFILT_PROC` / `NOTE_EXIT` observes selected processes but does not
  supply group membership. Historical `NOTE_TRACK` fork tracking is unsupported
  and is not a candidate authority.

Reports must disclose the actual platform identity and containment strength.
The abstraction must not claim stronger or identical semantics where the OS
does not provide them.

## Supervisor-Owned Group Anchor

The POSIX group anchor separates stable group identity from the workload root:

1. the supervisor launches and retains the exact direct-child anchor;
2. the anchor establishes the process group before workload target code starts;
3. the anchor remains alive, or intentionally unreaped, until membership is
   quiescent and no further group-directed termination can occur;
4. all destructive group operations are issued only while that stable anchor
   identity remains owned;
5. the membership backend, not group existence, decides quiescence;
6. the anchor is reaped only after that decision; bounded stream closure then
   completes against the now-quiescent owned set.

Keeping the leader as a zombie prevents PGID reuse, but it also keeps a
signal-zero group probe positive after all descendants have exited. Reaping it
restores numeric group emptiness but releases the identity for reuse. Therefore
an anchor closes the identity and termination races but cannot itself prove
descendant quiescence.

## Lifetime And Membership Leases

An inherited pipe or equivalent capability remains useful for owner death,
authorization EOF and cooperative lifetime signalling. It is not the sole
membership oracle. Ordinary descendant-launch APIs may close a file descriptor
that the intermediate child did not explicitly forward, producing EOF while a
descendant remains alive.

The formal lifecycle gate may rely on an inherited membership lease only if
propagation is made unavoidable by the launch boundary and a mutation proves
that an unleased descendant cannot execute. The current .NET descendant-launch
model does not provide that property. Until such a boundary exists, the lease is
supplemental and the platform membership backend remains authoritative.

## POSIX Lifecycle State Machine

```text
Prepared
  -> OwnerLifetimeBound
  -> AnchorIdentityEstablished
  -> ContainmentEstablished
  -> MembershipAuthorityEstablished
  -> Authorized
  -> Running
  -> TargetExitRecorded
  -> MembershipQuiescent
  -> AnchorFinalizedAndReaped
  -> StreamsDrained
  -> Completed

Any state
  -> Deadline | Cancellation | LaunchFailure | OwnerDeath
     | MembershipFailure | TerminateFailure | ReapFailure
  -> CleanupCommitted
  -> BoundedTerminateWhileAnchorIsStable
  -> MembershipQuiescent
  -> AnchorFinalizedAndReaped
  -> BoundedStreamDrain
  -> Failed
```

Unknown or unavailable membership state is failure, not quiescence. Cleanup
preserves every operation, termination, membership, reap and drain failure; a
later failure cannot replace earlier causal evidence.

## Reference And Behavioral Feasibility

The current design checkpoint is based on these primary contracts and isolated
probes; it does not authorize a POSIX implementation yet.

- The .NET Unix process wait implementation reaps an exited direct child through
  `waitpid`. Retaining a managed `Process` object therefore does not retain a
  zombie leader after the wait completes.
- Linux `kill(-pgid, 0)` reports group existence and permission, not stable
  membership identity. An isolated probe confirmed that a zombie leader keeps
  the group observable until `waitpid`, after which the numeric PGID can be
  reused.
- An isolated inherited-pipe probe observed EOF while an uncooperative but
  non-hostile grandchild remained alive because the intermediate launch did not
  forward the descriptor. A positive control that explicitly forwarded the
  descriptor delayed EOF until the grandchild exited.
- A delegated cgroup v2 probe retained `populated=1` after the root exited while
  a live descendant remained and was reparented; `cgroup.kill` then converged to
  `populated=0`.
- XNU's process-list implementation can filter `allproc` and `zombproc` by
  process group under the kernel process-list lock. Native macOS proof is still
  required because the exposed libproc interface is private and subject to
  change.

References:

- [.NET Unix process wait state](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessWaitState.Unix.cs)
- [.NET explicit inherited-handle allowlist](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessStartInfo.cs)
- [Linux `kill(2)`](https://man7.org/linux/man-pages/man2/kill.2.html)
- [Linux `pidfd_open(2)`](https://man7.org/linux/man-pages/man2/pidfd_open.2.html)
- [Linux `pidfd_send_signal(2)`](https://man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)
- [Linux cgroup v2](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html)
- [Apple libproc interface](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.h)
- [XNU process-group listing implementation](https://github.com/apple/darwin-xnu/blob/main/bsd/kern/proc_info.c)
- [XNU kqueue process-note contract](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/event.h)

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

The migration target removes these mechanisms from correctness paths. Paths
already removed during Stage 2 must not return while POSIX membership authority
is redesigned:

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
