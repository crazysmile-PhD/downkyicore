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

The first hosted macOS run exposed a legacy diagnostic-observer data-contract
bug before the macOS fixture executed: `Process.StartTime` may be null for a
`ps` row. The observer now preserves the existing optional `createdAtUtc` value
instead of dereferencing null; lifecycle ownership and success policy are
unchanged, and the child script output is retained by the behavioral assertion.

Strict PR CI run `32712033910` supplied the platform-native evidence on commit
`5506bc3c40a14e68a5be6e9234a42666276655be`: Windows, Linux and macOS build/test
jobs all passed. This includes the hosted Windows nested-Job assertion, normal
POSIX process-group execution and the launch-without-new-process-group mutation.
The run also exposed and closed two portability gaps before that evidence was
accepted: macOS named-pipe paths use bounded names, and the POSIX probe validates
the expected supervisor-PID group rather than accepting an inherited outer
group as ownership.

The same run's lifecycle job remained red only for the already classified
synthetic `Gate.ResidualChild/residual-child-self-test` fixture composition
failure (`CommandNotFoundException`). All 144 formal lifecycle phases passed.
Build, CodeQL and Protobuf workflows passed on the same commit. Automated Codex
review was requested for that exact commit, but the connector returned a usage
limit instead of a review; formal review therefore remains pending and the PR
must remain open.

No restart, lifecycle-phase, central-runner or forensics call site has migrated
in Stage 1.

A subsequent adversarial review removed the lease's remaining
`Kill(entireProcessTree: true)` fallback because it was a second process-tree
correctness owner. Failed execution now uses the platform containment owner,
reaps only the retained direct root, proves containment quiescence within the
same cleanup budget and then completes bounded stream drain. A deterministic
fixture starts both a root and a blocking descendant before cancellation; a
clean cancellation outcome is possible only after the lease's OS-backed
quiescence check succeeds. PID values in that fixture prove only that two
processes were started and are not a post-cleanup correctness oracle.

Strict PR CI run `32715016944` exercised this closure on commit
`e28b72836e61a8317121e6e9233e09b3c535df8a`. Windows, Linux and macOS
build/test jobs passed, as did Build, CodeQL, Protobuf, formatting, package
audit and every aria2 TLS matrix job. The only failed check remained the same
synthetic residual-child fixture (`CommandNotFoundException`); all 144 formal
lifecycle phases passed. Codex review was requested for the exact commit and
again returned a connector usage-limit response, so review remains pending.

## Stage 2: Lifecycle Phase Ownership

Move `Invoke-IsolatedProcess` launch, wait, owned-tree containment, termination,
reap and streams to the lease. Formal lifecycle correctness no longer depends on
PPID recursion.

Required proof:

- Unix parent exit and reparent do not lose an owned descendant.
- A residual child fails its real lifecycle phase.
- Windows phase correctness does not depend on WMI or PPID.
- Inherited stdout/stderr handles cannot cause an unbounded wait.

### Stage 2 Checkpoint

The consistency audit started from clean HEAD
`707746dd0b81fd5175c7614bf3b57f421693692c`. Current repository and hosted
Stage 1 evidence did not change the target architecture, trusted-child threat
model, restart Policy B or migration order. The parent-exit, Unix reparent,
inherited-stream and synthetic residual-fixture findings are evidence for the
existing lifecycle-phase migration, not evidence for a second owner or a
PID/PPID fallback.

`Invoke-IsolatedProcess` now creates one immutable `LaunchSpec`, one
`TransitionBudget` and one `OwnedProcessLease` before lifecycle target code can
execute. The lease owns launch, root wait/reap, Job Object or process-group tree
quiescence, bounded termination and concurrent stdout/stderr drain. Lifecycle
phase and process-exit success consume the typed lease outcome and fail closed
when ownership, operation, cleanup or quiescence fails. Central test-process
authorization remains a separate domain protocol and is completed against the
lease-provided target diagnostic identity; runner selection and canonical
arguments are unchanged.

The old correctness mechanisms have been removed from the formal lifecycle
path: `Get-ProcessTree`, `Get-ProcessIdentityKey`, `Get-LiveObservedProcess`,
`Wait-ResidualProcessTree`, the observed-child-release lease and direct
process-tree termination. WMI/`ps`, PID, PPID and start-time data remain only in
`Get-DiagnosticProcessTreeSnapshot` and evidence manifests. They cannot choose
a wait, kill or reap target, prove quiescence or convert failure to success.

Deterministic platform fixtures now execute a parent that exits while a blocking
descendant retains inherited streams, an ownership-establishment mutation, a
one-shot false-quiescence mutation and terminate/reap failure injection. The
same real lease path is exercised by formal `-ValidateForensics`; a non-quiescent
tree must classify as `ResidualChildProcess`, preserve typed ownership evidence
and complete bounded cleanup. Local Windows evidence before the Stage 2 commit:

- strict Release solution build passed for 23 projects with zero warnings and
  zero errors;
- targeted process-supervision behavioral/mutation tests and full Architecture
  tests passed through the central runner;
- the review-invariant gate passed 11 invariants, 318 tests and two intentional
  adversarial mutations;
- the platform selector passed and all eight Windows-owned test projects passed;
- lifecycle ownership found 607 matches and zero violations;
- one-assembly formal lifecycle `Local` run with `-ValidateForensics` produced
  nine successful phase results and zero failures;
- PowerShell syntax, module-boundary audit, formatting and `git diff --check`
  passed.

Hosted Windows, Linux and macOS proof and exact-HEAD review remain required
after the independently reviewable Stage 2 commit is pushed. The final Stage 2
exact HEAD is recorded after commit creation because a commit cannot contain
its own object ID.

## Stage 3: Forensics Observer

Move forensics to an observer and a formal evidence-hold supervisor sub-state.
Stage 2 already removed the PPID correctness oracle, synthetic
`ReleaseObservedChildren` owner and observed-child-release truth source.
Stage 3 completes observer/evidence-hold migration without reintroducing a
process owner, deadline owner or residual-process truth source.

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
