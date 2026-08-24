# PR #197 Process-Lease Migration

## Objective And Authority

- PR: [#197](https://github.com/crazysmile-PhD/downkyicore/pull/197)
- Exact starting HEAD: `98aadbe435b5cb5c847b7c1764e5e6a3a5923412`
- Design authority: `../design-docs/process-lifecycle-ownership.md`

This work does not patch one lifecycle P1. It closes the historical process
ownership failure family at an authoritative process-lease boundary. Product
restart Policy B, central test authorization, test-platform routing, TRX
semantics, release trust and lifecycle fail-closed behavior remain unchanged.

## Stage 1: Contracts And Platform Proof

Define `OwnedProcessLease`, `ParentLifetimeLease`, `TransitionBudget`, immutable
`LaunchSpec` and platform behavioral fixtures without changing domain semantics.

Required proof:

- Windows target code executes only after Job ownership is established.
- Linux/macOS target code executes only after process-group/session ownership.
- A Windows resume-before-Job-assignment mutation fails the behavioral gate.
- A Linux/macOS launch-without-new-process-group mutation fails the gate.

Platform primitives must be exercised, not asserted from source text. Stage 1
does not connect the new lease to restart, lifecycle, test runner or forensics.

### Stage 1 Checkpoint

The review boundary introduces `DownKyi.ProcessSupervision` with an inert launch
host, immutable launch payload, one monotonic operation/cleanup budget, typed
ownership metadata and bounded wait/terminate/reap/drain behavior. Control and
status use same-user named pipes, so supervisor launch does not require inherited
control handles. The target receives only its declared environment and standard
I/O endpoints after ownership is established.

Windows uses a named kill-on-close Job Object and a retained root process handle.
Linux and macOS use a pre-target-exec process group plus authoritative direct-child
wait/reap for the root. Metadata reports the actual Job name or process-group ID
and the platform's real containment strength.

Local Windows evidence at this checkpoint:

- strict Release solution build: 23 projects, zero warnings and zero errors;
- central Windows test solution: 8 of 10 platform-selected projects passed;
- lifecycle ownership audit: 605 matches and zero violations;
- platform fixture: normal ownership, ownership mutation, immutable launch spec,
  target-start failure, caller cancellation cleanup and monotonic budget passed.

Linux/macOS native process-group execution and GitHub-hosted Windows nested-Job
execution remain required CI evidence for the exact review commit. No restart,
lifecycle-phase, central-runner or forensics call site has migrated in Stage 1.

## Stage 2: Lifecycle Phase Ownership

Move `Invoke-IsolatedProcess` launch, wait, owned-tree containment, termination,
reap and streams to the lease. Formal lifecycle correctness no longer depends on
PPID recursion.

Required proof:

- Unix parent exit and reparent do not lose an owned descendant.
- A residual child fails its real lifecycle phase.
- Windows phase correctness does not depend on WMI or PPID.
- Inherited stdout/stderr handles cannot cause an unbounded wait.

## Stage 3: Forensics Observer

Move forensics to an observer and a formal evidence-hold supervisor sub-state.
On completion remove the PPID correctness oracle, synthetic
`ReleaseObservedChildren` owner and observed-child-release truth source. Stages
2 and 3 must not leave a permanent dual path.

## Stage 4: Restart Transaction

Retain prepare, authorize, commit and revoke. Keep product Policy B. The helper
uses `OwnedProcessLease`; exact parent lifetime uses `ParentLifetimeLease`.

The 30-second parent-wait product limit may remain, but it must be consumed
through the shared `TransitionBudget`, not an independent `WaitAsync` clock.

Required proof covers stale identity, PID reuse, authorization EOF, partial and
replayed authorization, parent exit before commit, parent hang after commit,
helper crash, cancellation, parent-wait failure, relaunch-start failure, desktop
handoff failure, and cleanup failure plus accepted handoff still committing.

## Stage 5: Central Test Runner

Share only the low-level process lease. Do not rewrite or weaken canonical
arguments, one-shot authorization, immutable invocation contracts, platform
routing or TRX semantic validation unless a direct contract conflict is proved.

## Stage 6: Legacy Removal

Remove remaining duplicate start, wait, kill, reap, timeout and process-identity
implementations. Update stable architecture documentation. Each stage must
remove its replaced owner before the next stage; no permanent fallback path is
allowed.

## Historical Windows Failure Classification

Run `32704078204` produced 144 successful formal phases:

```text
8 assemblies * 3 iterations * 6 phases = 144 formal phases
```

`Gate.Forensics` and `Gate.MarkerReader` succeeded. The only failure was the
synthetic `Gate.ResidualChild/residual-child-self-test`, recorded as
`CommandNotFoundException`.

The current classification is test-oracle/fixture-composition failure. It is
not a proved production lifecycle regression and must not be forced into the
PID/PPID root-cause family without evidence. The exact command remains unknown
because the report retained the exception type but not its message or stack.

On the exact starting HEAD, a local one-assembly `-ValidateForensics` run also
failed only this synthetic gate, while isolated `-ValidateObservedChildRelease`
passed. This further separates fixture composition from formal phase behavior.

## Completion Criteria

1. Every formal process owner establishes ownership before target code executes.
2. PID and PPID do not participate in correctness.
3. Each transition has one identity authority.
4. Each transition has one deadline owner.
5. Wait, terminate, reap and drain are bounded.
6. Restart never relaunches the wrong process.
7. Cleanup failure plus accepted desktop handoff retains Policy B.
8. Unix reparent does not change descendant ownership.
9. Windows correctness does not depend on WMI or PPID.
10. Handle inheritance cannot create an unbounded wait.
11. Forensics is not a second process owner.
12. Central test authorization and TRX invariants are not weakened.
13. Legacy correctness mechanisms are removed, not retained as fallbacks.
14. Mutating real launch-time ownership makes the formal gate fail.
15. Backend reports disclose actual identity and containment strength.
16. Windows, Linux, macOS and lifecycle CI converge on one exact HEAD.
17. Every migration stage is independently reviewable.
18. A changed HEAD invalidates earlier exact-HEAD clean reviews.

## Verification And Rollback

Each stage runs its platform behavioral and mutation tests first, then the
affected architecture and shared-runner regressions. Lifecycle migration also
runs formal cross-platform lifecycle CI. No retry, stdout filtering, timeout
weakening or fallback PID/PPID correctness is allowed.

Rollback is a whole-stage revert. Do not retain both a failed new owner and the
legacy correctness owner to make the gate green.
