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
       -> OwnedDiagnosticCollector

RestartTransaction
  -> RestartHandoffLease (candidate; Stage 4A proof required)
       -> ParentLifetimeLease
       -> typed one-shot authorization
       -> immutable cross-process deadline
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
It proves only exact parent exit; it does not own the helper, authorize or commit
restart, or create a deadline.

The ordinary `OwnedProcessLease` owner-death contract cannot directly own a
committed restart successor: owner lifetime EOF intentionally terminates and
reaps the ordinary owned set, while a committed restart helper must survive that
exact exit to perform one bounded relaunch attempt. Stage 4A therefore records
`Stage 4 original composition assumption invalidated` and tests a separate
restart handoff domain. See `restart-handoff-lifecycle.md`. This distinction does
not weaken or reopen the Stage 2 invariant.

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
- A delegated cgroup v2 child is the Stage 2 membership authority.
  Recursive
  `cgroup.events` `populated` state proves that no live process remains, and
  `cgroup.kill` provides atomic tree termination against concurrent forks and
  migration within the delegated subtree. Direct-child zombies remain the
  separate reap authority's responsibility.
- The backend must prove delegation and required files before authorizing target
  execution. A machine without that capability is unsupported for the formal
  lifecycle gate until another stable membership backend is designed and
  behaviorally proved.
- The supervisor initially joins the workload cgroup so target descendants
  inherit membership before authorization. After target exit is recorded, the
  still-live group anchor moves back to the delegated owner scope. The workload
  cgroup then contains descendants only, so `populated=0` can be proved without
  releasing the process-group identity or owner-lifetime channel.
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
- `proc_listpgrppids` is the Stage 2 authority for an atomic kernel membership
  snapshot of the anchored group. It must exclude the intentionally live anchor
  and prove zero remaining members before the anchor is reaped. The API is a
  private libproc interface. Native x64 and arm64 behavioral proof authorizes
  its use only with fail-closed runtime availability, buffer and error checks.
- The live anchor remains in the process group while the lease queries
  non-anchor membership. It exits only after the owner sends the finalization
  signal following a zero-membership proof; owner EOF instead terminates the
  anchored group.
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
6. the owner finalizes the still-live anchor only after that decision, except
   on Windows where kill-on-close Job ownership permits the anchor to exit
   before the Job active-process query;
7. the anchor is reaped only after that decision; bounded stream closure then
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
  -> AnchorExcludedFromMembershipSet
  -> MembershipQuiescent
  -> AnchorFinalized
  -> AnchorReaped
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

The owner-to-supervisor control pipe is retained through target exit. Closing
it at any point is owner death and triggers platform termination. A normal
finalization signal is accepted only after target exit and, on POSIX, after the
lease has proved authoritative membership quiescence. This closes the race in
which an anchor could exit after authorization while its owner died before
descendant cleanup.

## Reference And Behavioral Feasibility

The current design checkpoint is based on these primary contracts, isolated
probes and hosted native proof. It authorizes the platform membership backends
described below for Stage 2 implementation, subject to their fail-closed
capability preconditions. It does not claim that implementation is complete.

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
  process group under the kernel process-list lock. The exposed libproc
  interface is private and subject to change, so runtime availability and every
  query result must remain fail closed.

Hosted run [32980954766](https://github.com/crazysmile-PhD/downkyicore/actions/runs/32980954766)
provides the native feasibility decision:

- A GitHub Ubuntu 24.04 job starts in the root-owned
  `hosted-compute-agent.service` cgroup and cannot create a child directly. The
  preceding negative run failed closed with `EACCES`.
- The same unprivileged runner can request `Delegate=yes` from its systemd user
  manager. Inside that user scope it created and removed a child cgroup, retained
  `populated=1` after workload-parent exit and descendant reparent, terminated
  through `cgroup.kill`, converged to `populated=0`, and rejected an injected
  membership-query failure.
- Native `macos-15-intel` and `macos-15` jobs compiled against libproc. Both
  retained a group anchor, reaped the workload parent, observed the live
  reparented descendant through `proc_listpgrppids`, terminated the anchored
  group, proved zero non-anchor membership before anchor reap, and rejected an
  injected query failure.

The Linux systemd user scope is capability bootstrap, not a process or
membership truth owner. The backend must verify actual delegation and then use
only cgroup state for membership correctness. It must never invoke privileged
`sudo` setup. If the user manager, delegation, `cgroup.events` or `cgroup.kill`
is unavailable, launch fails before target authorization.

The macOS backend uses the retained direct-child anchor as stable group identity,
the process group for containment/termination, and `proc_listpgrppids` for
membership. A missing symbol, ambiguous zero/error result, exhausted buffer
growth or query error is failure, not quiescence.

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
Ownership attachment, immutable launch-payload writes and supervisor
finalization also consume the same remaining budget; pipe backpressure cannot
create an unbounded pre-launch transition.
The budget may reserve bounded cleanup time after the operation cutoff, but the
operation and hard-cleanup deadlines are established together by the same owner
on one monotonic timeline. Cleanup must not introduce a second clock owner.

A restart handoff crosses a process boundary without crossing into a new clock
authority. Prepare must fix an immutable absolute expiry in a platform
monotonic-clock domain. The successor may calculate only the remaining duration
from that expiry; it must not restart a stopwatch or allocate a fresh product
window. Stage 4A must prove the representation natively before it becomes a
production contract.

Caller cancellation may stop work before an irreversible transition. Once a
child has started or cleanup has begun, cleanup is not abandoned by caller
cancellation and remains bounded by the hard deadline.

## Forensics Boundary

`LifecyclePhaseSupervisor` owns process truth. `ForensicsObserver` consumes
snapshots and cannot kill a process, release a child, extend child lifetime,
create process ownership or extend the transition deadline.

When a deterministic fixture needs the target to remain available during
capture, `LifecyclePhaseSupervisor` requests an evidence hold from the same
`OwnedProcessLease` before launch. The lease creates and owns the hold endpoint,
injects it through the immutable launch payload, and accepts only a `Captured`
or `Failed` completion handoff. The actual held target must acknowledge that
handoff; the intermediary supervisor closes its inherited endpoint copies after
launch, so an unconsumed signal cannot validate the hold. `ForensicsObserver`
receives a diagnostic target ID and a caller-allocated
`DiagnosticCollectorWindow`; it never receives a target process lease,
containment handle, membership query, terminate target or deadline constructor.

A deterministic fixture may request this supervisor-owned sub-state:

```text
Running
  -> EvidenceHoldGranted
  -> EvidenceCaptured | EvidenceFailed
  -> Released
```

The hold is part of the supervisor state machine and the same transition budget.
Forensics failure may fail the phase but cannot prevent bounded cleanup.
The typed process outcome records whether the hold was requested, granted,
completed, released, delivered and acknowledged. Lifecycle reports keep `processFailureType`
and `forensicsFailureType` separate, so observer failure cannot replace the
owner's causal failure or turn it into success. `OwnedDiagnosticCollector`
owns collector start, wait, termination, authoritative reap and concurrent
stream drain on the attenuated window. PowerShell receives only typed evidence
or typed primary/cleanup failures and never receives the collector process or
cleanup target. The compiled collector has no authority over the observed
target or owned tree.

This independent boundary is implemented by
`6fc71e406ba80b2ccfbff49e05023f76f72458b6` and exact-head review-fix commits
`c3a3a33f67daa20ac450212433c69774385fb679` and
`e98c8a6c27cb22a44ffc63099af53a447139c6ee`, based on the fixed Stage 3 closure
`531399c375700d2bd188fe8723878fad008b7058`. The fixes preserve the first causal
collector failure, stream already-produced target evidence through the
supervisor before cleanup, carry typed collector failure/evidence/cleanup
fields into the lifecycle report and behaviorally reject whole-budget and
command-based PowerShell ownership mutations. Command ownership detection also
normalizes module-qualified PowerShell command names, while a shared
failure-to-report converter and JSON round-trip fixture prove that non-empty
cleanup stages and cause types survive the PowerShell boundary. Native
exact-head CI and a clean same-head review must converge after the latest fix;
Stage 4 production implementation and Stage 5 remain deferred. Stage 4A is the
separate restart-handoff feasibility checkpoint.

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
