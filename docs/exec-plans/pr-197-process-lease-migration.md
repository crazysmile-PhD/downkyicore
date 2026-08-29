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

### Stage 3 Implementation Checkpoint

Implementation commit:
`0d91f8f71d8567427f384ce39dc14ae52c60622b`.

The consistency audit found one remaining correctness owner in the forensics
path: `test-assembly-lifecycle.ps1` created and released an inherited capture
pipe directly, and its behavioral test asserted that this capture owner
controlled probe completion. The assembly probe also retained an unused
PID/creation-time residual-child release path with its own tree kill and wait.
Neither path supplied diagnostic value that required process ownership.

Stage 3 moves the optional evidence hold into `OwnedProcessLease`. The same
lease that established launch-time containment now creates the hold endpoint,
injects it through the immutable launch payload, records
`Requested -> Granted -> Captured | Failed -> Released`, and consumes the
existing `TransitionBudget` for the completion handoff. The PowerShell
`Invoke-ForensicsObserverCapture` function receives only a diagnostic target ID
and that existing budget. It cannot query authoritative membership, decide
quiescence, terminate, reap, release the hold or create a deadline. The unused
legacy residual-child release/kill implementation was removed from the probe.

Lifecycle report schema 4 preserves process and observer evidence separately.
`processFailureType` comes from the typed lease result;
`forensicsFailureType` and the capture-specific error fields remain concurrent
diagnostic evidence. A capture failure may fail the phase, but cannot replace a
lease failure or turn it into success. Observer-created `dotnet-stack` or `ps`
collector processes are bounded by the lease's original operation/cleanup
timeline and can terminate only their own collector process.

Local Windows evidence for the implementation content was collected before the
commit. The lifecycle machine report therefore records starting HEAD
`3ff37563dcb84c3f7e21fc5ea500339cb4ff2840` with
`workingTreeDirty=true`; the committed implementation is the same staged diff:

- strict Release solution build passed 23 projects with zero warnings and zero
  errors;
- focused Architecture tests passed 11/11 and focused Windows
  process-supervision tests passed 24/24;
- the review-invariant gate passed 11 invariants, 318 tests and two intentional
  adversarial mutations; re-enabling observer truth failed 1 of 2 mutation
  tests as required;
- the central Windows runner selected and passed all eight Windows-owned test
  projects: 1,080 passed, zero failed and one declared aria2 integration skip;
- lifecycle ownership reported 613 matches and zero violations;
- one-assembly `Local -ValidateForensics` reported schema 4, nine successful
  phase results and zero failures. It recorded capture-lead and evidence-hold
  validation true, an observer-missed descendant still rejected, injected
  observer failure preserved, and lease cleanup complete;
- PowerShell syntax, module-boundary audit, formatting and `git diff --check`
  passed.

Status: Stage 3 native Windows/Linux/macOS CI, Assembly Lifecycle and exact-head
Codex review are pending for the documentation checkpoint that records this
implementation. Stage 3 remains open. Stage 4 and Stage 5 findings remain
deferred, and no workflow, restart, central-runner, release or tag change is
included.

### Stage 3 Exact-Head Review Follow-up

The documentation checkpoint at
`570fc319d824e24e1012b4d377b5f95a7b1914f7` passed all 20 applicable GitHub
checks, including Windows/Linux/macOS Strict PR CI, Assembly Lifecycle, native
Linux/macOS membership feasibility, Build, CodeQL and Protobuf. Its exact-head
Codex review then found five Stage 3 gaps: collector waits did not accept
cancellation; a collector kill exception could skip bounded reap; undefined
capture-completion enum values were accepted; the held target did not
acknowledge the release signal; and the Architecture gate inspected only the
observer wrapper instead of its helper closure. None of these findings changed
the Stage 2 membership, quiescence, terminate or reap contract.

Follow-up implementation commit:
`7fc4a3a1ddd4c9d916edaefec09debc7c5fe29c2`.

The evidence hold is now a two-way lease-owned transaction. The owner sends the
defined `Captured` or `Failed` completion, the actual held target must return a
distinct acknowledgment, and the intermediary supervisor closes its inherited
endpoint copies immediately after target launch. A target that stays alive but
does not consume the handoff therefore cannot validate the hold. Diagnostic
collector exit and stream-drain waits accept cancellation, while any cleanup
path attempts kill and bounded reap independently and retains both failures.
The observer Architecture gate follows its transitive PowerShell helper closure;
only the bounded collector-cleanup helper may terminate the collector it
created, and a dedicated helper-authority mutation must make the gate fail.

Local validation for the follow-up implementation passed:

- strict Release solution build with all analyzers: 23 projects, zero warnings
  and zero errors;
- focused Architecture tests: 12/12; focused Windows process-supervision tests:
  25/25, including undefined completion and non-acknowledging target adversaries;
- review-invariant corpus: 11 invariants, 319 tests and three intentional
  adversarial mutations; observer truth and transitive helper authority each
  produced a real failed test;
- the routed Windows solution gate selected all eight Windows-owned projects:
  1,083 passed, zero failed and one declared aria2 integration skip;
- lifecycle ownership: 613 matches and zero violations; one-assembly
  `Local -ValidateForensics`: nine phase results, zero failures, with
  `TargetAcknowledged=true`, observer miss/failure preserved and cleanup
  complete;
- PowerShell syntax, JSON parsing, formatting verification over 967 files and
  `git diff --check` passed.

Status: Stage 3 remains open pending native CI, Assembly Lifecycle and a clean
same-head Codex review for the checkpoint that records this follow-up. Stage 4,
Stage 5, workflow, release and tag work remain deferred.

### Stage 3 Native CI Follow-up

The follow-up documentation checkpoint
`91cf5b582ef80978f3bb97004c7517b80abb6a1b` passed native Linux/macOS
membership, Linux/macOS Strict PR CI, Build guards, CodeQL, Protobuf and the
remaining applicable checks. Its original Strict PR CI run retained two
Stage 3 failures: Windows process-supervision expected only a timeout when an
uncooperative held target did not acknowledge completion, although the same
bounded owner transition may first observe acknowledgment-channel EOF after
deadline cleanup terminates that target; the lifecycle forensics self-test
started its capture-lead clock before lease establishment, so hosted-runner
startup could consume the synthetic 1.25-second threshold even though capture,
release and target acknowledgment all succeeded. Neither failure invalidated
the Stage 2 ownership contract.

Native-CI follow-up implementation commit:
`62cd3cdd9dec16571b03f14f0098010ac173ad98`.

The non-acknowledging-target regression now accepts only the two bounded,
fail-closed outcomes, `TimeoutException` or `EndOfStreamException`, and still
requires the lease outcome to report `OperationDeadlineExceeded`, completion
delivered, target not acknowledged and cleanup complete. The lifecycle gate
starts the capture-lead observation clock only after `OwnedProcessLease` has
established the target and returned the authoritative lease. The separate
adversarial owned-tree self-test now allocates a three-second operation window
to the same lease owner so runtime startup does not consume its fixture window;
the window remains bounded and the observer acquires no clock or cleanup
authority.

Local validation for this follow-up implementation passed:

- the focused Architecture forensics class and Windows owned-process-lease
  class passed through the central in-process runner after Release builds with
  zero warnings and zero errors;
- the exact-checkpoint review-invariant gate passed 11 root-cause invariants,
  seven test projects, 320 tests and all three intentional adversarial proofs;
- the one-assembly `PR -ValidateForensics` gate reported nine phase results,
  zero failures and lifecycle ownership 613/0, with capture lead, evidence
  hold, target acknowledgment, observer miss, concurrent observer failure and
  lease cleanup all validated;
- PowerShell syntax, formatting verification and `git diff --check` passed.

One exact-checkpoint Windows class run immediately after the invariant-heavy
gate exhausted the unchanged Stage 2 fixture-publication test's five-second
window before an authoritative target-exit timestamp was available. The same
25-test class then passed 25/25 in the bounded classification run, and that
unchanged test had passed in the original `91cf5b5` CI run whose sole Windows
failure was the Stage 3 acknowledgment oracle. No Stage 2 implementation or
test budget was changed. This is retained as load-sensitive local validation
evidence, not treated as a green retry or Stage 2 contract invalidation; the
new exact-head native and Strict PR gates remain required acceptance evidence.

Status: Stage 3 remains open pending required native/Strict PR CI, Assembly
Lifecycle and a clean exact-head Codex review for the documentation checkpoint
that records this implementation. Stage 4, Stage 5, workflow, release and tag
work remain deferred.

### Stage 3 Collector Budget Follow-up

The documentation checkpoint
`c43c00a32ce583cb48ba9a630af6fd5f97d9efff` passed every applicable exact-head
check except Assembly Lifecycle, including Windows/Linux/macOS tests, native
Linux/macOS membership, Build guards, CodeQL and Protobuf. Strict PR CI run
`33130433725`, Assembly lifecycle job `98718424813`, retained one Stage 3
failure. Its artifact recorded
`DownKyi.Core.Tests` execution exit 0, `ownedTreeQuiescent=true`, no process
failure, no cleanup failure and every lease/forensics ownership self-test true,
but the single diagnostic collector consumed 175,877 ms before reporting a
wrapped timeout as `MethodException`. The phase therefore failed closed as
`SlowEvidenceMissing`. This proves an observer capture-budget gap; it does not
invalidate the Stage 2 membership, quiescence, terminate or reap contract.

Collector-budget implementation commit:
`9bb9cdc537f642f62c5289b4c753c5f719b08da3`.

`Invoke-IsolatedProcess`, the caller that holds the existing
`TransitionBudget`, now allocates a 15-second capture window plus a five-second
collector-only cleanup window. Diagnostic delays, Windows snapshots, `ps`,
`dotnet-stack`, collector exit and stream drain consume the shorter of that
allocation and the original owner budget. Observer helpers can consume but
cannot create or renew the allocation. PowerShell timeout classification walks
the wrapped exception chain, while collector kill and reap append independently
to the same failure list. A behavioral self-test launches a permanently blocked
collector and requires bounded timeout, collector-only cleanup and remaining
owner operation budget. The review-invariant corpus includes a fourth mutation
that restores direct whole-budget collector waits and must fail the Architecture
gate.

Local validation for this implementation passed:

- the focused Architecture lifecycle classes passed through the central
  in-process runner after a Release build with zero warnings and zero errors;
- the capture-budget mutation produced the required failed Architecture test;
- the review-invariant gate passed 11 root-cause invariants, seven test
  projects, 321 tests and all four intentional adversarial proofs;
- one-assembly `Local -ValidateForensics` reported nine phase results, zero
  failures and lifecycle ownership 613/0. The blocked-collector self-test,
  capture lead, evidence hold, observer-missed descendant, concurrent observer
  failure and lease cleanup all passed; normal managed-stack capture completed
  in 5,668 ms;
- PowerShell syntax, JSON parsing, formatting verification and
  `git diff --check` passed.

Status: Stage 3 remains open pending the documentation checkpoint, exact-head
push, all required native/Strict PR and Assembly Lifecycle checks, and a clean
same-head Codex review. Stage 4, Stage 5, workflow, release, merge and tag work
remain deferred.

### Stage 3 Collector Exact-Head Review Follow-up

Documentation checkpoint `8b112eb673333837675a6895578a4188597286ac`
passed all 29 exact-head checks: 20 applicable jobs succeeded and nine
release-only jobs were skipped. This included Windows/Linux/macOS tests,
Assembly Lifecycle, native Linux/macOS membership, CodeQL, Build, Protobuf,
format and package audit. Exact-head Codex review `5047152038` then identified
three Stage 3 blockers: the capture subwindow used a rollback-sensitive wall
clock; the capture-budget mutation changed only a C# source string instead of
executing the broken helper; and a collector timeout was omitted when cleanup
also failed. None invalidated the Stage 2 ownership contract.

Exact-head review follow-up implementation commit:
`467c3cbc15c9512e1c89ef4e44e8235524f47af4`.

The owner-allocated subwindow now uses a caller-started monotonic `Stopwatch` and
durations, always intersected with the existing monotonic `TransitionBudget`.
Observer helpers read that allocation but cannot start or renew it. The corpus
mutation now changes the executed PowerShell wait helper to consume the whole
owner budget, launches the real lifecycle script, and is rejected by the
blocked-collector behavioral self-test. PowerShell `VoidTaskResult` values from
waits are suppressed so they cannot turn a false self-test into a truthy output
array. When timeout and collector cleanup failure coincide, the timeout and
every cleanup exception are retained in the same aggregate; the self-test
injects that conjunction while still requiring bounded collector reap and
remaining owner budget.

Local validation for this implementation passed:

- focused Architecture lifecycle classes passed through the central in-process
  runner after a Release build with zero warnings and zero errors;
- the behavioral whole-budget mutation executed the real lifecycle helper and
  produced the required failed Architecture test after the child self-test
  failed closed;
- the review-invariant gate passed 11 root-cause invariants, seven test
  projects, 321 tests and all four adversarial proofs;
- one-assembly `Local -ValidateForensics` reported nine phase results, zero
  failures and lifecycle ownership 614/0. Monotonic collector-window,
  timeout-plus-cleanup aggregation, capture lead, evidence hold, observer miss,
  concurrent observer failure and lease cleanup self-tests passed; normal
  managed-stack capture completed in 5,658 ms;
- PowerShell syntax, formatting verification and `git diff --check` passed.

Status: Stage 3 remains open pending the documentation checkpoint, exact-head
push, all required native/Strict PR and Assembly Lifecycle checks, review-thread
closure and a clean same-head Codex review. Stage 4, Stage 5, workflow, release,
merge and tag work remain deferred.

### Stage 3 Evidence Outcome Review Follow-up

Documentation checkpoint `2602a8cecc8f06fcaf4733aa8f57c6f53d21076c`
passed all 29 exact-head checks: 20 applicable jobs succeeded and nine
release-only jobs were skipped. This included Windows/Linux/macOS tests,
Assembly Lifecycle, native Linux/macOS membership, CodeQL, Build, Protobuf,
format and package audit. Exact-head Codex review `5047334281` then identified
two Stage 3 blockers: the capture-budget mutation test failed whether the real
self-test rejected or accepted the broken helper, and `WaitAsync` could freeze
an evidence-hold outcome before an already-started acknowledgment publication
completed. Both behaviors came from Stage 3 observer/evidence-hold work and did
not invalidate the Stage 2 ownership contract.

Evidence-outcome follow-up implementation commit:
`8f9c70becac64c3b15458255893ab46b7c3a55cf`.

The adversarial outer test now expects only a successful child exit. A correct
lifecycle self-test therefore makes the mutation proof red by rejecting the
broken whole-budget helper, while an incorrect acceptance would let the outer
test pass and make the review-invariant gate itself fail. `OwnedProcessLease`
now freezes late hold completion atomically and, when completion already
started, waits within its existing `TransitionBudget` for that transaction to
settle before constructing the immutable outcome. A deterministic native test
holds acknowledgment publication after target exit and proves `WaitAsync`
cannot complete until publication is released. This changes no Stage 2 launch,
containment, membership, quiescence, terminate, reap or stream-drain contract.

Local evidence for the implementation:

- focused Architecture behavior tests passed through the central runner;
- Windows `OwnedProcessLeasePlatformTests` passed 26/26, including the
  deterministic acknowledgment-publication race;
- the whole-budget behavioral mutation produced one intended failure among
  five Architecture tests because the real self-test returned exit 1; no
  blocked collector remained afterward;
- the full review-invariant gate passed 11 invariants, seven projects, 321 tests
  and all four adversarial proofs;
- one-assembly `Local -ValidateForensics` reported nine phase results, zero
  failures and lifecycle ownership 614/0. Capture-window, capture-lead,
  evidence-hold, observer miss/failure and lease cleanup self-tests passed;
- PowerShell syntax, JSON parsing, formatting verification and
  `git diff --check` passed.

Status: Stage 3 remains open pending exact-head push, required native/Strict PR
CI, Assembly Lifecycle, closure of the two fixed review threads and a clean
same-head Codex review. Stage 4, Stage 5, workflow, release, merge and tag work
remain deferred.

### Stage 3 Outcome Synchronization Review Follow-up

Documentation checkpoint `2cfe8b998b14733d69d5f5c8c506ab4f0999f3e7`
passed all 29 exact-head checks: 20 applicable jobs succeeded and nine
release-only jobs were skipped. This included Windows/Linux/macOS tests,
Assembly Lifecycle, native Linux/macOS membership, CodeQL, Build, Protobuf,
format and package audit. Exact-head Codex review `5047495756` then identified
four Stage 3 blockers: unrelated child failures could satisfy the new mutation
oracle; failure outcomes did not await already-started hold completion; a hold
settlement timeout retained the preceding stream-drain failure kind; and the
success-race regression released its gate before proving `WaitAsync` reached
the outcome-snapshot boundary. None invalidated the Stage 2 ownership contract.

Outcome-synchronization follow-up implementation commit:
`1298bf5cb4bcb69c2a5cb69ce07204ba782f51e8`.

The mutation proof now recognizes only a nonzero child exit carrying the exact
capture-window self-test rejection. Unrelated build, platform, access, I/O,
cancellation or timeout failures let the outer mutation test pass so the corpus
rejects the ineffective proof; normal tests cover both false classifications.
`OwnedProcessLease` resets the pending owner failure kind before hold settlement
and synchronizes already-started completion for both success and failure
outcomes. Ownership failure still performs terminate/reap first, then uses only
the cleanup portion of the same `TransitionBudget`; settlement failure is
preserved beside the causal process failure. Internal native-test latches prove
both paths reached the actual snapshot boundary before acknowledgment
publication is released.

Local evidence for the implementation:

- focused Architecture behavior tests passed 5/5 through the central runner;
- Windows `OwnedProcessLeasePlatformTests` passed 27/27, including deterministic
  success and caller-cancellation failure snapshot races;
- the whole-budget mutation produced exactly one intended failure among five
  Architecture tests and carried the exact rejection text; no blocked collector
  remained before or after the run;
- the full review-invariant gate passed 11 invariants, seven projects, 321 tests
  and all four adversarial proofs;
- one-assembly `Local -ValidateForensics` reported nine phase results, zero
  failures and lifecycle ownership 614/0. Capture-window, capture-lead,
  evidence-hold, observer miss/failure and lease cleanup self-tests passed;
- PowerShell syntax, JSON parsing, formatting verification and
  `git diff --check` passed.

Status: Stage 3 remains open pending the documentation checkpoint, exact-head
push, required native/Strict PR and Assembly Lifecycle checks, closure of these
four fixed review threads and a clean same-head Codex review. Stage 4, Stage 5,
workflow, release, merge and tag work remain deferred.

### Stage 3 Formal-Phase Attach/Shutdown Diagnostic Checkpoint

Strict PR run `33165929460` failed only Assembly Lifecycle because the
`DownKyi.Core.Tests` iteration 2 target exited after approximately 4.038 seconds
while a synchronous `dotnet-stack 9.0.661903` attach consumed the full existing
15.019-second capture window. Empty tool streams, complete collector reap/drain,
the teardown markers and deterministic accepted-but-unanswered diagnostics-pipe
proof classify the slow transition as attach during target shutdown, not a
process-lease or collector cleanup failure.

Implementation commit `35606b5cdbd7a011b7a515fd7a6aa28c8c4f9039`
adds the lease owner's monotonic `TargetExitedAfter` measurement and read-only
`TargetExitedToken`. Lifecycle policy uses the token only to cancel observer
work and uses the timestamp only for phase duration. The observer still receives
no lease, process truth, containment, membership, terminate/reap authority or
deadline constructor. The pinned 15-second window, parent transition budget and
single collector owner are unchanged. Detailed artifact, transition, mutation
and local validation evidence is recorded in
`pr-197-owned-diagnostic-collector-migration.md`.

Status: Stage 3 remains open pending the documentation checkpoint, exact-head
CI and same-head review. Stage 4A remains completed and is not reopened;
production Stage 4 and all later stages remain deferred.

First exact-head run `33171141030` proved the formal correction: all 12 slow
phases captured evidence, `SlowEvidenceMissing` was zero and the attach-stall
self-test passed. Its two failures were proof-fixture defects: the unlinked
mutation's 600 ms allowance did not reach ready on hosted Windows, and temporary
mutation-report cleanup could replace a child result without preserving its
exception type. Test-only follow-up
`63cbd5d2e310ebc28330999f153cadf27a334552` reuses the existing three-second
hosted-start allowance and makes best-effort report cleanup diagnostic-only.
It changes no lease, observer, collector, timeout or Stage 4A contract. Exact
evidence and follow-up validation remain in
`pr-197-owned-diagnostic-collector-migration.md`.

Second exact-head run `33173057693` preserved the typed attach-stall evidence
but exposed an invalid proof comparison: the fake target's connection duration
and the collector's transition duration came from different monotonic origins,
and the oracle incorrectly demanded 2.5 seconds after tool process start even
though startup belongs to the caller-owned three-second window. Test-only
follow-up `13207a0d4e546068367279bea8932aea8c292ac7` aligns request, pipe
acceptance and typed return with cross-process UTC timestamps, measures window
consumption from request creation, and retains wrapper exception types in xUnit
output. It changes no process-lease production contract, timeout, retry,
ownership boundary or Stage 4A status. Exact timing, CI and validation evidence
is authoritative in `pr-197-owned-diagnostic-collector-migration.md`.

Third exact-head run `33174514303` proved the attach-stall oracle and target-exit
duration correction but exposed a downstream classification defect. Two genuine
slow phases returned typed `CallerCancelled` after `StackOutputFirstByte` and
complete collector reap/drain, with 7,866 and 15,008 stack characters already
present; lifecycle policy nevertheless discarded the stack and reported
`SlowEvidenceMissing`. Script-only follow-up
`07b076a7ef69e75e831c84ef9b5b12f8d066fb8f` retains that evidence under a
strict typed predicate and keeps empty cancellation or unrelated failure kinds
fail closed. It changes no lease, deadline, ownership, production or Stage 4A
contract. Exact artifact and validation evidence is authoritative in
`pr-197-owned-diagnostic-collector-migration.md`.

Fourth exact-head run `33176593640` exposed two remaining proof-oracle defects.
The attach-stall caller interval was 3,006 ms, but its compiled collector
timeline started later and reported 2,844.331 ms; the oracle incorrectly treated
that later origin as the caller-window clock. The Windows capture-budget
mutation also stopped on an unrelated missing-tool precondition, so its owning
test correctly stayed green and the outer corpus failed closed. Test-only
follow-up `155df31624d732b72c010d52c89a01f5287fd209` measures the already-recorded
outer request/typed-return UTC interval and lets the independent capture-window
fixture reach its exact rejection before the real-tool requirement is checked.
Normal Windows forensics still requires the pinned tool before attach or formal
capture. No process-lease contract, timeout, ownership, workflow, production or
Stage 4A boundary changed. Exact CI and local evidence is authoritative in
`pr-197-owned-diagnostic-collector-migration.md`.

Fifth exact-head run `33178177118` proved the formal correction across all eight
assemblies and 147 phases: all six slow phases captured evidence, missing
evidence was zero, attach-stall passed and ownership remained 633/0. Its only
failed job was a Windows fixture ordering gap after Architecture and review
invariants had passed. The child could publish its blocking-ready file and make
the target exit before the collector owner recorded `ProcessStarted`, producing
typed `CallerCancelled` with `Evidence.Started=false`. Test-only follow-up
`f38cd23bf2f8acc3978d11adcedbace3d236e2e8` separates the ready and target-exit
signals and exposes a nonthrowing internal completion source only after owner
start observation. It changes no public collector API, process-lease contract,
timeout, ownership, workflow, production or Stage 4A boundary. Exact CI and
local validation evidence is authoritative in
`pr-197-owned-diagnostic-collector-migration.md`.

### Stage 3 Parent-Budget Self-Test Blocker Checkpoint

Starting exact head `2953afc4c259ae0a81a7f787d74a7e53fad7966e`
failed only Assembly Lifecycle because the compiled collector had completed
typed timeout, reap and drain with no cleanup failure, but the self-test required
more than 1,000 ms of its five-second parent and observed 962.370 ms. Design and
history classify 1,000 ms as a self-test proof threshold: Stage 3's contract is
an attenuated child on the same monotonic `TransitionBudget` with a strictly
positive parent operation remainder, not a one-second safety reserve.

Implementation `9c8f9765ca207116324a776c27ed973184710756` replaces
the inherited hosted timing margins with direct child-exhausted and
parent-still-positive predicates, and retains caller-timing/deadline-authority
diagnostics. The whole-parent mutation executes the same lifecycle self-test and
produces one owning Architecture failure while timeout, authoritative
reap/drain, empty cleanup and all unrelated predicates remain valid. Focused,
affected and full Architecture, review corpus and one-assembly lifecycle gates
passed locally; exact counts and timing evidence are authoritative in
`pr-197-owned-diagnostic-collector-migration.md`.

This checkpoint changes no collector timeout, root timeout, retry, sleep,
deadline owner, target-process owner, slow-evidence policy or Stage 4 restart
handoff implementation. Stage 4 closure and same-head review remain blocked
until naturally triggered required CI is green. Stage 5, merge, release and tag
movement remain prohibited.

## Stage 4A: Restart Handoff Feasibility

The original composition assumption is invalidated. An ordinary
`OwnedProcessLease` must terminate and reap its owned set when the owner lifetime
ends; a committed restart helper must instead survive that exact parent exit,
attempt one relaunch and then terminate. No existing lease transition transfers
that ownership without weakening the Stage 2 invariant.

Stage 4A tests a separate bounded restart handoff domain. `ParentLifetimeLease`
proves only exact parent exit. Typed authorization, one-shot commit, immutable
cross-process deadline and terminal helper behavior belong to the restart
domain; they must not become an `IgnoreOwnerDeathAfterCommit` switch on the
ordinary lease. The detailed checkpoint is
`pr-197-stage-4a-restart-handoff-feasibility.md` and the design boundary is
`../design-docs/restart-handoff-lifecycle.md`.

The 30-second parent-wait product limit may remain only as one absolute
monotonic deadline fixed at prepare. The helper consumes remaining time and may
not acquire a fresh `WaitAsync` or stopwatch window.

Stage 4A completed its native proof at exact source head
`689c5d6c41b3a3a7b8a0c6a318c80a4ebe737879`. Strict PR run
[33161523853](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853)
passed the Windows process-handle, Linux pidfd and macOS armed-kqueue backends.
Each platform passed 16/16 restart-handoff cases and 4/4 shared IPC-naming cases,
including authorization, identity, immutable-deadline, exactly-once and
terminal-helper mutations. Assembly Lifecycle reported 627 ownership matches,
zero violations, zero failed phases and zero residual children.

The accepted architecture is a separate bounded restart handoff domain; it is
not an ordinary-lease transfer or owner-death exception. The ordinary lease
contract remains unchanged. The macOS path failure was resolved as a shared IPC
naming invariant: logical labels are diagnostic-only, while all repository pipe
servers use `IpcEndpointName` physical identifiers of 21 ASCII characters under
a 24-character ceiling with 80 bits of randomness. This Stage 4A evidence remains
the feasibility authority and is not reopened by the production migration.

## Stage 4: Production Restart Handoff Migration

Production Stage 4 replaces the `ProcessRestartLauncher` PID/start-time helper,
anonymous commit byte and raw helper cleanup owner with a typed
`RestartHandoffLease` transaction. `DesktopApplication` recognizes the new
typed helper protocol before Avalonia startup, and
`AvaloniaApplicationLifecycle` retains Policy B through the existing desktop
handoff and failure aggregation contract. The coherent implementation and
executable-proof commit is `12fbde8647d0a8ddc907264f3ab10741f84e966a`.

The pre-commit lease owns candidate launch, watcher/readiness proof, typed
authorization, commit/revoke and bounded helper cleanup/reap. The committed
helper is a distinct restart-domain successor. It retains one native exact-parent
watcher, consumes the immutable deadline created by the caller's single
`TransitionBudget`, attempts relaunch once and exits. Windows uses a retained
`SYNCHRONIZE` process handle, Linux uses pidfd plus poll, and macOS uses an armed
kqueue process-exit note. Unsupported native capability fails closed with no
numeric identity fallback.

The implementation removes the replaced product path instead of retaining a
fallback. Ordinary `OwnedProcessLease` still terminates and reaps on owner EOF;
no detach, transfer or `IgnoreOwnerDeathAfterCommit` state was added. Physical
pipe names remain generated by `IpcEndpointName`.

Local implementation evidence on Windows passed:

- 32 production/Stage 4A restart cases through the central project runner;
- 30 affected architecture cases and all 328 Architecture cases;
- 23 `ProcessRestartLauncher` and Avalonia lifecycle/Policy B cases;
- all 90 tests in the routed Windows platform project;
- the review-invariant gate: 333 normal tests and all 15 adversarial proofs;
- eight Stage 4 mutation profiles, each with eight executed tests and exactly
  one owning-invariant failure;
- Linux and macOS production-test project compilation;
- one-assembly Local lifecycle sanity: 644 ownership matches, zero violations,
  nine successful phases, zero failures and zero residual children.

The first pushed documentation checkpoint
`13926b2190716537ada92d6dc242ed535e6b4dcd` naturally triggered Strict PR run
[`33225293262`](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33225293262).
Its Ubuntu Architecture step failed before the ordinary-lease mutation could
launch the fixture: the generic runner's systemd cgroup did not delegate
directory creation under `/sys/fs/cgroup`, so `OwnedProcessLease` correctly
rejected preparation with `UnauthorizedAccessException`. This was a mutation
test capability error, not a pidfd restart-watcher or production handoff
failure. The failed workflow was not rerun.

Test-only follow-up commit
`a1ac9b1ee5b37a66ec16529678c7aef5707b9513` treats that exact Linux rejection as
the stronger normal fail-closed outcome. With the ordinary-lease mutation
active, the same unavailable authority remains an explicit invariant failure.
Windows, macOS and delegated Linux still execute the original owner-EOF
successor-kill proof. No production lease, workflow, delegation, deadline,
retry or Stage 4A contract changed. Local Windows validation passed the normal
mutation class 8/8, the ordinary-lease mutation with eight executed and exactly
one failed test, all 328 Architecture tests, and the complete review-invariant
gate with 333 normal tests and all 15 adversarial proofs. Formatting and
`git diff --check` also passed.

That was the pre-closure status of the Stage 4 implementation. Stage 4 is
`CLOSED` at the authoritative Stage 5 starting head
`dd6364b7e713d3b3c81efd739821cc7e0baafe86`; Stage 5 does not modify or reopen
the production handoff, native watcher, deadline, Policy B, authorization,
commit or revoke paths. Stage 6 remains deferred.

## Stage 5: Central Test Runner

Implementation/proof commit
`a768a9d86bba3b5bd0f0834a7997cf21a9ccd017` migrates repository test-child
execution to the existing `OwnedProcessLease`. Marker-isolation follow-up
`8ecba8f0fc86dd10d70ce309c9101379873a8ba6` makes lifecycle-marker write
authority one-shot so nested proof processes cannot corrupt the outer target's
teardown evidence. These commits do not reopen Stage 2, Stage 3, Stage 4A or
Stage 4.

The compiled `DownKyi.CentralTestRunner` retains project/platform routing,
canonical xUnit arguments, one-shot authorization issuance, immutable
invocation hashing, aggregate orchestration and TRX validation. One
caller-created `TransitionBudget` covers authorization handoff and the test
process lease. `OwnedProcessLease` / `SupervisorHost` exclusively own start,
pre-execution containment, wait, terminate, reap, quiescence, streams and
cleanup. PowerShell wrappers retain only typed argument forwarding, fixed
compiled entrypoint invocation, logging and result propagation.

Authorization uses a random current-user named endpoint plus token and complete
argv hash. The assembly guard requires the exact one-frame protocol and EOF,
clears the transport environment, rejects replay, wrong token/hash,
partial/empty input and the legacy numeric pipe-handle environment. The
supervisor transports opaque launch metadata and has no test authorization or
policy dependency. Linux delegation is established before project/solution
mode selection and the shared lease remains the only containment/membership
owner; no PID/process-enumeration fallback was added.

The focused executable proof covers authorization success/failure, owned
normal/hung/cancelled/supervisor-failure children, owner EOF, stream drain,
TRX failure despite exit zero and zero-test rejection. Ten review-corpus
mutations each require exactly one owning test failure: raw `Process.Start`,
numeric HANDLE/fd transport, supervisor authorization, guard bypass, replay,
fresh deadline, private cleanup, exit-code-only TRX success, zero-test success
and Linux enumeration fallback.

The detailed owner table, transport contract, validation evidence and closure
state are authoritative in
`pr-197-stage-5-central-test-runner.md`. Stage 5 remains an open implementation
checkpoint until its documentation commit is pushed, required Windows/Linux/
macOS CI passes naturally on one exact head, the same head receives a clean
Codex review and the worktree is clean. Stage 6 remains deferred.

Initial documentation head
`5177d4605e12fbeb0039e8ef27434d8062938051` naturally triggered Strict PR run
`33232585457`. Successful format, audit, CodeQL, protobuf, FFmpeg, delegated
Linux and macOS-membership jobs did not override four Stage 5 integration
findings: direct review execution missed Linux delegation, prebuilt aria2 jobs
could not bootstrap a missing compiled provider under target `NoBuild`, a
Windows source oracle assumed LF and the macOS launch fixture orphaned a child
after killing only its term-resistant root. No failed job was rerun and no
same-head review was requested.

Follow-up `86d99537a8a360edce00be16d666f96c3dda93c1` fixes only those proven
boundaries and adds their policy/mutation proof. The run's separate unchanged
Stage 3 slow-evidence ordering self-test `AggregateException` was not folded
into Stage 5. Replacement exact-head CI and same-head review remain required;
Stage 6 stays deferred.

Exact follow-up head `83d62b8ad39c6ca0aaeaaabca595d76077191a59`
naturally triggered run `33234070529`. Windows, Ubuntu, all six aria2 jobs and
all other Stage 5 integration gates passed. macOS again produced a 93/93 TRX
followed by `OwnedTreeNotQuiescent`, disproving the release-verifier process-
group attempt. Commit `4691e100826c760a0578568242ffa3350bca14df`
restores that out-of-scope release tooling exactly and makes only the
TERM-resistant test app single-rooted. The repeated Stage 3 lifecycle self-test
failure remains separate; no rerun or review was requested.

Next documentation head `86f9ac51c73856b1734337872fed6e22456748ba`
naturally triggered run `33235099993`. All Stage 5 integration gates except
macOS Strict passed; macOS again produced a 93/93 TRX followed by
`OwnedTreeNotQuiescent`, so the single-root fixture did not identify the
residual. The unchanged Stage 3 self-test also repeated before formal phases.
Diagnostic follow-up `3a909f22e465da94d91c09964c582b84478d0ee0`
preserves typed captured output and adds a diagnostics-only macOS assembly-end
group-member observer. It does not kill, reap, decide quiescence or reopen the
closed Stage 3/4 contracts. Replacement exact-head CI remains required; no
failed run was rerun and no review was requested.

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
