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

At reviewed implementation HEAD `6b1f9fdc9b1defb0fe7cc59040c4a1351911c72b`,
`Invoke-IsolatedProcess` creates one immutable `LaunchSpec`, one
`TransitionBudget` and one `OwnedProcessLease` before lifecycle target code can
execute. The lease owns launch, root wait/reap, Job Object or process-group tree
quiescence probing, bounded termination and concurrent stdout/stderr drain. The
review described below later proved that the POSIX probe was not an adequate
quiescence authority. Lifecycle
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
descendant retains inherited streams, an ownership-establishment mutation,
fail-closed membership-query mutation and terminate/reap failure injection. The
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

Stage 2 implementation exact HEAD is
`b69758b336513d13d4deddf006ceb178d78860b3`. Strict PR CI run
`32951764061` passed the Windows, Linux and macOS build/test jobs, the formal
Assembly Lifecycle Stability gate, format, package audit and all six aria2 TLS
jobs. Build, CodeQL and Protobuf checks also passed for this exact code HEAD.

The first hosted macOS execution of the migration exposed one implementation
defect inside the existing POSIX containment invariant: Darwin may return
`EPERM` from a signal-zero process-group probe while an unsignalable group
member still exists. The lease had treated that result as a terminal probe
error. Quiescence now requires `ESRCH`; `EPERM` remains non-quiescent and keeps
polling within the same `TransitionBudget`, while unknown errors still fail
closed. The real parent-exit/descendant fixture and deterministic errno-decision
tests cover this correction. No owner, truth source, fallback or deadline was
added.

The exact-HEAD review completed and found that the POSIX backend still used one
numeric process group as stable identity, containment/termination target and
membership/quiescence oracle. A retained or zombie group leader prevents PGID
reuse but makes signal-zero group existence permanently non-quiescent; reaping
the leader restores the empty-group result but releases the numeric identity for
reuse. This is new evidence that changes the POSIX authority model, so the stop
rule applies. The Windows Job Object model and the higher-level supervision
architecture remain valid.

Status: Stage 2 remains reopened. Stage 2A native backend feasibility is now
complete; the reviewed implementation and its earlier CI evidence do not yet
complete POSIX lifecycle ownership. Stage 3 has not started.

### Stage 2A: POSIX Identity And Membership Feasibility

Exact reviewed implementation HEAD:
`6b1f9fdc9b1defb0fe7cc59040c4a1351911c72b`.

The revised authority model separates:

| Contract | Windows | Linux candidate | macOS candidate |
| --- | --- | --- | --- |
| Stable anchor identity | retained process handle | pidfd plus retained direct child | retained direct child/wait state |
| Containment/termination | Job Object | pre-exec process group; prefer `cgroup.kill` | pre-exec process group while anchor remains owned |
| Membership/quiescence | Job active-process state | delegated cgroup v2 recursive `populated` | `proc_listpgrppids` snapshot excluding anchor |
| Reap | retained direct-child handle | direct-child wait/reap after membership closure | direct-child wait/reap after membership closure |

The supervisor-owned process-group anchor is accepted only as a stable identity
guard for group-directed termination. It is not the membership oracle. An
inherited owner-lifetime pipe remains useful for owner death and authorization
EOF, but it is not an authoritative descendant lease because ordinary child
launch APIs may close a descriptor that was not explicitly forwarded.

Reference and isolated behavioral evidence:

- Linux preserved process-group existence while the group leader was a zombie
  and returned `ESRCH` only after the leader was reaped. This proves the anchor
  closes PGID reuse only while it also defeats signal-zero quiescence.
- A child that did not explicitly propagate an inherited lease descriptor
  produced EOF while its grandchild was still alive. Explicit propagation was
  the positive control and held EOF until grandchild exit.
- In a delegated WSL cgroup v2 child, a live reparented descendant kept recursive
  `populated=1` after the root exited; `cgroup.kill` converged to `populated=0`.
  The same machine's top-level scope was not writable, confirming that
  delegation is a capability to prove rather than assume.
- Linux pidfd supplies stable task identity and, on newer kernels, stable
  process-group signalling. It does not supply descendant membership.
- macOS XNU can list group members, including `allproc` and `zombproc`, under a
  kernel process-list lock. The public stability, buffer/error contract and
  hosted-runner behavior of `proc_listpgrppids` are not yet proved.
- kqueue fork tracking is rejected because `NOTE_TRACK`, `NOTE_TRACKERR` and
  `NOTE_CHILD` are documented as unsupported on current macOS.

Recorded isolated probe outcomes:

```text
group anchor:     { alive: true, state: Z, zombie: true, afterReapErrno: 3 }
lease omitted:    { descendantAlive: true, childReady: true, leaseEof: true }
lease forwarded:  { descendantAlive: true, earlyEof: false, lateEof: true }
cgroup root exit: { rootExited: true, populated: 1, afterCgroupKill: 0 }
```

These probes establish the model boundary, not production platform acceptance.
They were run outside the repository and did not modify implementation files.

Native feasibility must prove:

1. Linux hosted lifecycle environments expose a delegated cgroup v2 subtree
   that can be created before target authorization, reports reparented live
   descendants through recursive `populated`, supports bounded `cgroup.kill`,
   and can be deterministically removed. Direct-child zombie collection remains
   a separate retained-handle/reap proof.
2. macOS x64 and arm64 can use `proc_listpgrppids` to distinguish an intentionally
   live anchor from live, zombie and reparented descendants, with bounded
   retry/buffer behavior and fail-closed handling of unsupported or permission
   errors.
3. Anchor launch and containment establishment occur before target code, and a
   mutation of either transition makes the formal gate fail.
4. Owner-lifetime EOF cannot make membership quiescent while the platform
   membership backend reports a descendant.
5. Termination, membership convergence, anchor reap and stream drain consume one
   `TransitionBudget` and preserve concurrent failure evidence.

The isolated proof harness is intentionally outside the formal lifecycle
implementation:

- `script/process-supervision-feasibility/linux-cgroup-membership.py` requires
  an unprivileged delegated cgroup v2 subtree and exercises parent exit,
  reparent, live descendant membership, `cgroup.kill`, `populated=0` convergence
  and an injected membership-query failure.
- `script/process-supervision-feasibility/macos-group-membership.c` retains a
  direct-child group anchor, reaps the workload parent, observes the reparented
  live descendant through `proc_listpgrppids`, terminates the anchored group,
  proves membership convergence before anchor reap and injects query failure.
- `.github/workflows/process-membership-feasibility.yml` runs the Linux proof on
  `ubuntu-24.04` and the macOS proof on both `macos-15-intel` and `macos-15`.

These files are feasibility evidence only. They do not make the current POSIX
production backend correct or provide a fallback path.

#### Stage 2A Hosted Evidence And Decision

Run [32980768700](https://github.com/crazysmile-PhD/downkyicore/actions/runs/32980768700)
at proof HEAD `fb3aca98883ba0f2e9753b5fc96aa95f41dcf331` established the negative
Linux boundary. The Ubuntu 24.04 runner was user `runner` in root-owned
`/system.slice/hosted-compute-agent.service`; creating a child cgroup returned
`EACCES` and the proof failed closed. Both macOS jobs passed on the same run.

Run [32980954766](https://github.com/crazysmile-PhD/downkyicore/actions/runs/32980954766)
at proof HEAD `53c22344316a839b3bd0f292372fecb1ed733537` then exercised the
unprivileged systemd user-manager delegation boundary. All three jobs passed:

- `ubuntu-24.04`: `systemd-run --user --scope -p Delegate=yes` supplied a
  delegated user scope. The probe established parent exit/reparent while the
  descendant remained live, authoritative `populated=1`, `cgroup.kill`, bounded
  convergence to `populated=0`, deterministic subtree removal and injected
  membership failure with exit 42.
- `macos-15-intel`: native x64 compile, live reparented membership,
  group termination, zero non-anchor membership, anchor reap and injected
  failure all passed.
- `macos-15`: the same native proof passed on arm64.

Stage 2 implementation is therefore authorized only with these backend
constraints:

1. Linux formal lifecycle execution first acquires an unprivileged delegated
   systemd user scope. The lease verifies delegation and creates its own child
   cgroup before target authorization. `cgroup.events` is membership authority;
   `cgroup.kill` is the preferred tree termination primitive. The user manager
   grants capability but is not a second membership truth source.
2. macOS retains the exact direct-child group anchor until all destructive group
   operations and zero non-anchor `proc_listpgrppids` membership are complete.
   The private libproc dependency is verified at runtime and any unavailable,
   ambiguous or failed query fails closed.
3. Process groups remain containment/termination primitives only. PID, PPID,
   numeric PGID polling and inherited-lease EOF remain prohibited as correctness
   fallback.
4. The existing Stage 2 implementation acceptance criteria numbered 3 through
   5 above remain mandatory behavioral/mutation proof during implementation;
   backend feasibility does not satisfy them by itself.

### Stage 2 Authoritative Backend Implementation Checkpoint

Status: Stage 2 implementation commit
`af2c02440e253273a7add923c5ca815083d43d98` closes the macOS termination-result
defect described below. The last fully reviewed and CI-validated head remains
`d955fb54b9cd7cbea0608277397124d56b5a89ad`; native CI and exact-head review for
the correction are still pending. Stage 3 has not started.

`OwnedProcessLease` now establishes and acknowledges containment plus membership
before accepting the immutable target launch payload. Linux creates a per-lease
child in a delegated cgroup v2 scope, moves the inert supervisor into it before
authorization, moves the still-live supervisor back to the delegated owner
scope after target exit, and uses recursive `cgroup.events` `populated` state as
the only membership/quiescence authority. macOS retains the direct-child group
anchor and uses bounded `proc_listpgrppids` snapshots, excluding only that live
anchor, as the membership authority. Process groups remain termination
primitives. Windows keeps Job active-process state as its existing authority.

The launch-spec write, ownership-ready handshake, target-exit report,
finalization, membership convergence, termination, reap and stream drain all
consume the caller's one `TransitionBudget`. The owner control channel remains
open as a lifetime capability; EOF triggers platform termination. The target
exit timestamp is emitted immediately from supervisor target-wait state before
membership, cleanup, stream drain or forensics latency. Fixture publication now
closes and flushes its staging handle before atomic rename and removes staging
state on every failure path.

Removed or downgraded correctness paths:

- signal-zero numeric process-group existence no longer decides POSIX
  quiescence;
- PID, PPID, numeric PGID and inherited EOF remain diagnostics or lifetime
  signals only and have no membership fallback role;
- lifecycle process-exit timing no longer samples wall time after lease cleanup;
- target launch cannot progress before the ownership-ready acknowledgement;
- launch-pipe backpressure cannot outlive the caller's operation budget.

Local evidence for this implementation tree:

- strict Release solution build passed with zero warnings and zero errors;
- all 17 Windows process-supervision behavioral/mutation cases passed;
- Architecture tests passed through the central runner;
- the platform selector passed and all 8 of 10 Windows-applicable test projects
  passed; the packaged aria2 TLS test remained an expected local skip because no
  packaged binary was supplied;
- lifecycle ownership found 615 matches and zero violations;
- a one-assembly formal lifecycle `Local` run with `-ValidateForensics` produced
  9 successful phase results and zero failures;
- PowerShell syntax, actionlint for all 11 workflows, formatting and
  `git diff --check` passed.

The behavioral suite covers target parent exit with a retained descendant,
authoritative membership convergence after termination, membership-query
failure, owner EOF both before and after target exit, authorization refusal when
ownership establishment fails, a stalled 900 KB launch payload, inherited
stdout/stderr, target-exit timestamp isolation and publication cleanup. Mutations
break ownership establishment, membership authority, macOS retained-anchor
ordering, budget propagation and terminate/reap stages through the real lease
path.

The first native implementation run
[33040956098](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33040956098)
at checkpoint HEAD `41d57a7281391934a8e19ed3182cb3139d333cac` passed the delegated
Linux cgroup proof and both macOS feasibility jobs, but its Linux repository
test failed. The exact failure was a legacy one-shot mutation that returned a
false quiescent result, authorized anchor release, and only then relied on an
inherited stream to expose the lie. That mutation bypassed the new authoritative
membership boundary and made bounded cleanup platform-dependent. Commit
`54e2b6c6c7cb8d7d667fd947052e6997cc27c59d` removes the false-success mutation.
The real inherited-stream fixture now remains behind authoritative membership,
fails as `OwnedTreeNotQuiescent`, and proves bounded terminate, reap and stream
closure. Membership-query failure remains the fail-closed authority mutation.

The next native run
[33041707054](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33041707054)
at checkpoint HEAD `0c1ef1324f1e4c533b78c2692520245c7f2160b1` passed the Linux
repository tests and both macOS feasibility jobs. Its macOS repository test
failed only because the retained-anchor mutation asserted one exception-message
spelling instead of the typed lease outcome. Commit
`efa22aaa5e98f51842e1e63d7ec9ec83e0aee3bf` makes that mutation macOS-specific
and verifies failure kind, non-quiescence, authoritative target exit and
preserved cleanup failures. It also removes the erroneous Linux dependency on
anchor reap: the delegated cgroup path remains Linux membership authority until
the lease disposes it, independently of process-group anchor state.

Exact-HEAD review then exposed three Stage 2 implementation gaps within the
existing lease and cleanup invariants. Commit
`3d72fa62db67c409d5316fa1487073613c15eb9c` closes them without adding a
membership owner or deadline authority:

- lifecycle and solution entrypoints now use one delegated-cgroup scope helper,
  so direct Linux lifecycle execution acquires the same `Delegate=yes`
  capability before creating a lease;
- containment preparation returns a staged resource owner before membership
  attachment. A post-attachment failure therefore kills and reaps the inert
  supervisor, then disposes the same cgroup owner; rollback and directory
  cleanup failures remain visible;
- the valid cgroup namespace root `/` resolves to the authority root while path
  escape remains rejected.

Deterministic regressions execute delegated argument forwarding, cgroup-root
resolution and post-membership-attachment failure cleanup. Local Windows
validation passed the strict solution build with zero warnings/errors, 19
process-supervision cases, 6 delegated-scope behavior cases, all 308 Architecture
tests, all 8 Windows-selected projects and a one-assembly lifecycle run with 9
phase results and zero failures. PowerShell syntax, actionlint, formatting and
`git diff --check` also passed.

Exact-head closure evidence:

- Strict PR CI run
  [33061861548](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33061861548)
  passed Windows, Linux and macOS build/test, all six packaged aria2 TLS jobs,
  Architecture/format/package checks and the 6 minute 36 second Assembly
  Lifecycle gate;
- Process Membership Feasibility run
  [33061861443](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33061861443)
  passed delegated Linux cgroup v2 and native macOS x64/arm64 membership proof;
- CodeQL run
  [33061861417](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33061861417)
  passed;
- the Codex exact-head review at
  [d955fb54b9cd](https://github.com/crazysmile-PhD/downkyicore/pull/197#issuecomment-5437591194)
  found no major issue.

The following documentation-only head triggered Strict PR CI run
[33062685229](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33062685229).
Its macOS job exposed a Stage 2 termination-result defect in
`OwnerLifetimeClosureTriggersBoundedOwnedTreeCleanup`: after owner EOF had
already caused the supervisor to signal its process group, the retained group
could contain only zombies and no live signalable member. Darwin returned
`EPERM`, and the parent cleanup incorrectly retained that syscall result as a
termination failure even though libproc membership remained the authoritative
quiescence decision.

The current correction maps Darwin's no-signalable-member `EPERM` to a completed
termination request only for the libproc-backed macOS path. It does not report
tree success: `OwnedProcessLease` must still obtain authoritative libproc
convergence, and unknown errors or unavailable/ambiguous membership continue to
fail closed. Executable decision regressions accept success and `ESRCH`, defer
only Darwin `EPERM`, reject the same result without Darwin authority and reject
unknown errors. The existing native owner-EOF behavioral test and membership
failure mutation remain the production-boundary proof. Stage 2 stays open until
the corrected exact head passes native CI and review.

The local Windows aggregate runner exposed a separate Stage 2 test-oracle
constraint while validating this correction: the launch-payload budget mutation
used a 400 ms operation budget, which could expire during supervisor startup
before reaching the intended bounded pipe-write boundary. The fixture now uses
a three-second operation budget while retaining the exact launch-specification
failure oracle and the single shared `TransitionBudget`. This does not add a
retry, sleep, second deadline or production behavior; it keeps the mutation
bounded while requiring it to fail at the intended production boundary.

Local validation for implementation commit
`af2c02440e253273a7add923c5ca815083d43d98` passed strict Release builds of the
Windows and macOS platform test projects, 24/24 Windows process-supervision
tests (including the five termination-result decisions), 308/308 Architecture
tests, the platform-selector regression, and all eight Windows-owned projects
through `test-solution.ps1`. PowerShell syntax, formatting verification and
`git diff --check` also passed. Native Darwin behavior still requires the next
exact-head macOS CI run.

### Unresolved Review Thread Ownership

GitHub thread state is not Stage 2 authority. At implementation commit
`3d72fa62db67c409d5316fa1487073613c15eb9c`, the 15 unresolved review threads
classify as follows:

Stage 2 findings closed by native CI and exact-HEAD review, although the GitHub
threads remain unresolved review history:

- direct Linux lifecycle execution must acquire delegated cgroup scope;
- cgroup namespace membership `/` must resolve to the delegated authority root;
- failed cgroup membership attachment must preserve cleanup evidence and remove
  the staged directory after bounded supervisor reap.

Already superseded by current Stage 2 implementation, with the thread retained
only as historical review state:

- POSIX quiescence no longer uses numeric group identity after anchor reap;
- failed fixture publication removes its staging file;
- launch-specification writes consume `TransitionBudget` asynchronously;
- the POSIX owner-lifetime channel remains open after launch and EOF terminates
  the owned workload;
- target exit time comes from the supervisor target-wait state before cleanup;
- ownership readiness is acknowledged before target authorization or launch
  payload progress.

Deferred by design and not part of Stage 2 closure:

- workflow dependency skipping and required-OS matrix closure belong to central
  workflow/test execution ownership in Stage 5;
- retaining descendants from `test-project-runner.ps1` belongs to the Stage 5
  central runner migration;
- restart-helper termination/reap ordering belongs to Stage 4 Restart
  Transaction;
- exact SQLite analyzer allowlist paths belong to the SQLite ownership policy,
  outside this process-lease migration;
- recovery workflow process-supervision input closure belongs to release trust,
  outside this migration.

No deferred thread may be pulled into Stage 2 merely because GitHub still marks
it unresolved. Conversely, an already-superseded thread is accepted only when
the current-head behavior and mutation proof remain green.

If Linux delegation or the macOS membership primitive is unavailable or cannot
prove membership, the backend fails before authorization or completion. No
PID/PPID/PGID fallback is permitted. Remaining Stage 3 work is limited to the
documented observer/evidence-hold migration; it must not gain process ownership,
a membership truth source or a deadline authority.

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
