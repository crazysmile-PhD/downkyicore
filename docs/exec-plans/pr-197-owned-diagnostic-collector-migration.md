# PR #197 Owned Diagnostic Collector Migration

## Objective And Current Facts

This plan tracks an independent PowerShell-boundary follow-up to the completed
PR #197 Stage 3 work. Local implementation and validation are complete; native
exact-head CI and same-head review are not yet complete.

- PR: [#197](https://github.com/crazysmile-PhD/downkyicore/pull/197)
- Owner branch: `fix/pr196-review-followup`
- Exact planning HEAD: `531399c375700d2bd188fe8723878fad008b7058`
- Planning commit: `27ac6da102855fdbc647335aaf91133c1bacab1e`
- Implementation commit: `6fc71e406ba80b2ccfbff49e05023f76f72458b6`
- Exact-head review-fix commit: `c3a3a33f67daa20ac450212433c69774385fb679`
- Stage 3 implementation: `1298bf5cb4bcb69c2a5cb69ce07204ba782f51e8`
- Design authority: [Owned Diagnostic Collector](../design-docs/owned-diagnostic-collector.md)
- Parent process design: [Process Lifecycle Ownership](../design-docs/process-lifecycle-ownership.md)
- Parent migration plan: [PR #197 Process-Lease Migration](pr-197-process-lease-migration.md)

At the planning HEAD, local, origin and PR HEAD were equal and the worktree was
clean. Stage 3 remains closed. PR #197 remains open, unmerged and unreleased.
The implementation and documentation checkpoints must still be pushed and
proved on one later exact HEAD.

The checkpoint moves only the lifecycle of diagnostic collector children into
a compiled owner. `OwnedProcessLease` remains the sole target-process owner.
Stage 4, Stage 5, workflow, release and tag work remain deferred.

## Exact Source Inventory

The migrated closure began in
[`test-assembly-lifecycle.ps1`](../../script/test-assembly-lifecycle.ps1).
The inventory below records the boundary used for the completed local
migration; it is not a second live implementation.

### Move To C# And Delete After Caller Migration

| Function | Current responsibility | Replacement point |
| --- | --- | --- |
| `New-OwnerAllocatedForensicsCaptureWindow` | Starts the PowerShell stopwatch and stores 15-second operation and five-second cleanup policy | Lifecycle owner allocates `DiagnosticCollectorWindow` from the existing `TransitionBudget` |
| `Get-ForensicsCaptureWaitMilliseconds` | Intersects owner budget and PowerShell capture stopwatch | Compiled window exposes bounded remaining operation/cleanup time |
| `Test-ForensicsExceptionType` | Traverses aggregate/inner exceptions | Typed collector failure and cleanup enums |
| `Test-ForensicsTimeoutException` | Detects timeout by exception type traversal | `DiagnosticCollectorFailureKind.OperationDeadlineExceeded` |
| `Invoke-BoundedForensicsCollector` | Starts collector, waits, drains streams and aggregates failures | `OwnedDiagnosticCollector.CollectAsync` |
| `Stop-BoundedForensicsCollector` | Kills, reaps and appends cleanup failures | Compiled collector cleanup state machine |
| `Test-ForensicsCollectorCaptureWindow` | PowerShell-only blocked-collector self-test | Platform executable and mutation tests in C# |

Phase 1 adds the replacements but does not delete these functions. Phase 2
switches every caller and deletes them in the same implementation checkpoint;
there is no permanent fallback switch.

### Temporarily Retain In PowerShell

| Function | Why it remains | Required Phase 2 change |
| --- | --- | --- |
| `Resolve-DiagnosticsTool` | Resolves operator/CI tool location; it does not own a child lifecycle | Continue returning only an executable path |
| `Get-DiagnosticProcessTreeSnapshot` | Formats diagnostic PID/PPID observations; those observations are not process truth | Replace only its Unix `ps` child invocation with the compiled collector outcome |
| `Save-ManagedStack` | Chooses stack evidence destination and interprets tool-specific exit/output | Invoke compiled collector and consume typed outcome/failure; never receive collector `Process` |
| `Save-ProcessEvidence` | Builds evidence files and target thread/snapshot diagnostics | Keep formatting only; do not add target process authority or collector cleanup |
| `Invoke-ForensicsObserverCapture` | Owns lifecycle capture policy and diagnostic status mapping | Allocate/pass the typed window and call migrated helpers; do not aggregate collector exceptions |
| `Protect-ProcessDiagnosticText` | Redacts evidence text | No ownership change |

The temporary PowerShell caller may continue to observe a target diagnostic ID
or diagnostic-only target snapshot. It must not receive the collector's raw
`Process`, containment handle, membership authority or terminate/reap action.

### Defer To A Lifecycle Runner Checkpoint

| Function | Reason for deferral |
| --- | --- |
| `Get-TransitionBudgetWaitMilliseconds` | It is also used by `Save-OwnedTreeEvidence`; it is not specific to collector child ownership |
| `Wait-ForensicsObserverDelay` | The delay is lifecycle capture policy rather than collector process lifecycle; until a lifecycle runner owns it, it must consume the typed caller-allocated window without creating or renewing a clock |
| `Save-ProcessEvidence` | Eliminating its diagnostic target `Process` object is broader than collector-child ownership |
| `Invoke-ForensicsObserverCapture` | Moving the complete observer policy/result state machine would be a lifecycle-runner rewrite |

These functions are not justification for keeping collector start, wait, kill,
reap, stream drain or exception aggregation in PowerShell.

### Test-Only Helper Disposition

`Test-ForensicsCollectorCaptureWindow` is a test-only helper embedded in the
production lifecycle script. Phase 1 first replaces its proof with compiled
tests. Phase 2 removes the helper together with the old collector closure. Phase
3 preserves the real lifecycle invocation as integration evidence without
reintroducing a self-test-only process owner.

## Expected Implementation Files

This section describes possible future modifications. None are changed by this
planning checkpoint.

### Phase 1 Files

- [`tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs`](../../tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs): add or host allocation of a non-renewable collector window from the existing `TransitionBudget`; do not change target ownership semantics.
- `tools/DownKyi.ProcessSupervision/DiagnosticCollectorContracts.cs`: add the collector-specific request, window, evidence, outcome, failure and cleanup contracts without conflating them with `OwnedProcessOutcome`.
- `tools/DownKyi.ProcessSupervision/OwnedDiagnosticCollector.cs`: add the compiled collector lifecycle and deterministic disposal owner.
- [`tools/DownKyi.ProcessSupervision/SupervisorHost.cs`](../../tools/DownKyi.ProcessSupervision/SupervisorHost.cs): add only deterministic collector fixtures that cannot be expressed by the existing block-forever probe, such as large output, inherited-stream retention or parent-exit descendant behavior.
- `tests/ProcessSupervisionTestCases/OwnedDiagnosticCollectorPlatformTests.cs`: add the shared executable behavioral and mutation corpus.
- [`tests/DownKyi.Windows.Tests/DownKyi.Windows.Tests.csproj`](../../tests/DownKyi.Windows.Tests/DownKyi.Windows.Tests.csproj), [`tests/DownKyi.Linux.Tests/DownKyi.Linux.Tests.csproj`](../../tests/DownKyi.Linux.Tests/DownKyi.Linux.Tests.csproj) and [`tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj`](../../tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj): link the new shared platform test file. SDK default compile inclusion means the process-supervision project file should change only if a real build/reference requirement is proved.

`OwnedProcessLease.cs` is not an assumed Phase 1 edit. Composition should use
its public contract. If collector implementation requires weakening or changing
the Stage 2 target lease, the checkpoint stops for design review.

### Phase 2 Files

- [`script/test-assembly-lifecycle.ps1`](../../script/test-assembly-lifecycle.ps1): switch `ps` and `dotnet-stack` collector calls to the compiled owner, consume typed results and remove the replaced collector lifecycle functions.
- [`tests/DownKyi.Architecture.Tests/AssemblyLifecycleProbeBehaviorTests.cs`](../../tests/DownKyi.Architecture.Tests/AssemblyLifecycleProbeBehaviorTests.cs): update executable integration/mutation coverage so the real lifecycle caller proves the compiled boundary.
- [`tests/DownKyi.Architecture.Tests/AssemblyLifecycleArchitectureTests.cs`](../../tests/DownKyi.Architecture.Tests/AssemblyLifecycleArchitectureTests.cs): add a structured architecture rule that PowerShell cannot reconstruct raw collector process ownership or a second deadline.

No Stage 4, Stage 5, release or workflow file belongs in the first
implementation checkpoint.

### Phase 3 Documentation Files

- [Process Lifecycle Ownership](../design-docs/process-lifecycle-ownership.md): update current ownership only after the compiled caller is active.
- [Assembly Lifecycle Stability Gate](../testing/assembly-lifecycle-stability.md): replace the PowerShell collector implementation description with the executable compiled contract and current proof.
- This plan and [Owned Diagnostic Collector](../design-docs/owned-diagnostic-collector.md): record implementation and closure evidence without altering the existing Stage 3 evidence.

## Milestones

### Phase 1: Typed Collector Core

1. Add collector-specific immutable contracts and caller-allocated window.
2. Add `OwnedDiagnosticCollector` with one compiled lifecycle state machine.
3. Reuse `LaunchSpec`, the parent `TransitionBudget` timeline and, where its
   contract fits without weakening, internal `OwnedProcessLease` behavior.
4. Add unit and platform executable tests before changing the PowerShell caller.
5. Keep the existing PowerShell path authoritative during this phase; the new
   core is not yet a second production fallback or selectable implementation.

Phase 1 does not rewrite the lifecycle runner, marker reader, protocol
classification, central test runner or Linux delegation.

### Phase 2: Lifecycle Caller Migration

1. Allocate the collector window at the lifecycle policy boundary.
2. Replace both `dotnet-stack` and Unix `ps` raw child handling with
   `OwnedDiagnosticCollector.CollectAsync`.
3. Consume typed outcome/failure fields; do not inspect exception messages.
4. Ensure PowerShell never receives the collector's raw `Process`, kill/reap
   target or mutable cleanup list.
5. Delete the replaced start/wait/kill/reap/drain/deadline/aggregation functions
   in the same migration commit.
6. Run the real lifecycle behavioral and mutation gate through the migrated
   caller.

### Phase 3: Legacy Verification

1. Add a structured PowerShell-AST or equivalent architecture gate that rejects
   raw collector `ProcessStartInfo`/`Process` construction, kill/reap calls and
   independent collector deadline construction in the lifecycle closure.
2. Mutate that gate with representative forbidden ownership and prove it fails.
3. Update stable design/testing documentation and this inventory.
4. Search all collector callers and prove there is no fallback environment
   variable, feature flag or retained legacy function.
5. Complete native CI, same-head review and closure evidence on one exact HEAD.

The architecture gate supplements, but never replaces, real process behavior.

## Required Behavioral Proof

Every process behavior below must be executable. Source-text assertions may
protect architecture shape, but cannot establish start, wait, cleanup, reap or
stream behavior.

| Scenario | Proof class | Required observation |
| --- | --- | --- |
| 1. Normal collector success | Unit plus executable behavioral | Started, exited, reaped and streams drained; exit 0; empty cleanup list |
| 2. Nonzero collector exit | Unit plus executable behavioral | Completed typed outcome retains exact nonzero code and streams; lifecycle policy classifies it |
| 3. Permanently blocked collector | Executable behavioral and native | Operation window expires; terminate/reap/drain remain bounded |
| 4. Caller cancellation | Unit plus executable behavioral | `CallerCancelled` remains primary; post-start cleanup completes under hard allowance |
| 5. Operation timeout | Unit plus mutation | `OperationDeadlineExceeded`, `TimedOut=true`, no renewed budget |
| 6. Terminate failure | Injected executable behavioral; native backend where supported | Terminate failure retained; reap attempt still occurs |
| 7. Reap timeout | Native-only proof | Reap deadline is typed; PID observation cannot report success |
| 8. Stream-drain timeout | Executable behavioral and native | Exited is not completion; undrained inherited stream fails boundedly |
| 9. Timeout plus cleanup failure | Unit plus executable mutation | Timeout remains primary and every cleanup failure is retained |
| 10. Empty cleanup list | Unit | Immutable collection has count zero and is never `null` |
| 11. Large stdout/stderr | Executable behavioral on every OS | Concurrent drain completes without pipe backpressure deadlock or output mixing |
| 12. Parent exit with residual collector descendant | Native-only proof | Owned collector set is terminated and quiescent; no residual collector remains |
| 13. Owner retains operation budget | Executable behavioral and mutation | Short collector window expires while parent `TransitionBudget.RemainingOperation` remains positive |
| 14. Ignore caller-allocated window | Adversarial mutation | The same real behavioral gate becomes red if the collector consumes the whole parent budget |
| 15. Restore PowerShell process/deadline owner | Structured Architecture mutation | A fixture that constructs raw collector process state or a second deadline makes the Architecture gate red |

Tests must assert typed enums, evidence fields and list contents. Exception
message substrings may assist diagnostics but cannot be the pass/fail contract.

## Local Implementation Checkpoint

Implementation commit `6fc71e406ba80b2ccfbff49e05023f76f72458b6`
establishes the local review boundary:

- `TransitionBudget` allocates an attenuated `DiagnosticCollectorWindow` on the
  same `TimeProvider` and parent operation/cleanup timeline;
- `OwnedDiagnosticCollector` composes the existing process lease internally and
  exposes only collector-specific outcome, evidence, primary-failure and
  immutable cleanup-failure contracts;
- the compiled supervisor explicitly drains and relays target stdout/stderr, so
  the real `dotnet-stack` collector and the large dual-stream fixture use the
  same bounded owner path;
- lifecycle PowerShell allocates capture policy and consumes typed results. The
  seven replaced start/wait/kill/reap/deadline/exception helpers are deleted;
- Windows, Linux and macOS projects compile the same shared collector behavior
  corpus; structured Architecture mutations reject observer authority,
  restored raw PowerShell ownership and a renewed collector deadline.

Local validation during the implementation and documentation follow-up
produced:

- strict Release solution build: 23 projects, zero warnings and zero errors;
- focused collector-window and existing process-lease classes: pass through the
  central in-process runner;
- full Architecture project: 311 passed, zero failed;
- review invariant corpus: 11 invariants, seven projects, 321 tests and four
  effective adversarial proofs;
- one-assembly `Local -ValidateForensics`: nine phase results, zero failures,
  lifecycle ownership 619/0, with collector-window, capture-lead,
  evidence-hold, process-lease and reporter self-tests all passing;
- routed Windows solution tests: 1,102 passed, zero failed and one existing
  packaged-aria2 TLS test skipped because `DOWNKYI_ARIA2_BINARY` was absent;
- PowerShell parsing, platform-selector regression and `git diff --check`:
  pass.

This is local evidence, not native exact-head closure. Windows/Linux/macOS CI,
Assembly Lifecycle CI, Strict PR CI and same-head Codex review remain pending.
No stop rule was triggered locally; Stage 4, Stage 5, workflow, release, merge
and tag work remain deferred.

## Exact-Head Review Fix

Codex review `5048306320` inspected exact checkpoint
`59339c3cb60118d5a6913c1c370b885b2bd306a4` and reported five blocking findings
inside this migration boundary. Review-fix commit
`c3a3a33f67daa20ac450212433c69774385fb679` makes these corrections:

1. `DOWNKYI_TEST_MUTATE_FORENSICS_CAPTURE_BUDGET` now runs the actual lifecycle
   script. Its typed blocked-collector self-test must preserve parent operation
   budget and rejects the whole-budget mutation with the exact fail-closed
   contract.
2. The transitive PowerShell AST scan covers `CommandAst` and the mutation uses
   `New-Object`, `Start-Process`, `Stop-Process` and `Wait-Process` forms.
3. Collector start-failure classification uses the caught primary exception;
   cancellation arriving after that causal failure cannot reclassify it.
4. The PowerShell boundary unwraps `DiagnosticCollectorExecutionException`
   from the PowerShell invocation wrapper and the lifecycle report preserves
   typed failure kind, evidence and cleanup fields.
5. The supervisor relays target stdout/stderr as they are read. Cancellation or
   failure cleanup therefore retains evidence produced before termination.

Local proof after the review fix:

- strict Release solution build: 23 projects, zero warnings and zero errors;
- focused Windows collector/window tests: 16 passed, zero failed;
- focused lifecycle Architecture classes: 15 passed, zero failed;
- full Architecture project: 312 passed, zero failed;
- review-invariant corpus: 11 invariants, seven projects, 322 tests and all four
  adversarial proofs; the whole-budget and command-AST mutations each failed
  only their intended ownership assertion;
- one-assembly Local `-ValidateForensics`: nine phase results, zero failures and
  ownership 619/0. The report contains typed `OperationDeadlineExceeded`
  evidence with reaped/drained true and an empty cleanup list;
- Windows routed suite: eight projects, 1,105 total, 1,104 executed/passed, zero
  failed and one existing packaged aria2 TLS skip because
  `DOWNKYI_ARIA2_BINARY` was not supplied;
- PowerShell parsing, `dotnet format --verify-no-changes` and `git diff --check`
  passed.

Required native exact-head CI and a new clean same-head Codex review remain the
closure boundary. Review `5048306320` itself is not acceptance evidence because
it contains the findings fixed above.

## Second Exact-Head Review Fix

Exact head `0322d1c63f41d64a3d940d7792ba8f00d08a1259` passed 20 native/shared
CI checks; nine release-only jobs were skipped as designed and no check failed.
Codex review `5048688459` then reported two additional in-scope gaps, so that
head is evidence but not closure. Implementation commit
`e98c8a6c27cb22a44ffc63099af53a447139c6ee` makes the bounded follow-up:

1. The PowerShell `CommandAst` guard extracts the leaf command name before
   matching. Module-qualified `Microsoft.PowerShell.Management\New-Object`,
   `Start-Process`, `Stop-Process` and `Wait-Process` forms are rejected, and
   the executable helper-authority mutation now injects those exact forms.
2. `Invoke-ForensicsObserverCapture` and the lifecycle self-test use one typed
   failure-to-report converter. The PowerShell fixture JSON-round-trips a
   non-empty cleanup list and asserts both stage and cause type. A dedicated
   mutation that discards the list fails only the typed-report invariant.

Local proof for this follow-up produced:

- focused Architecture class: six passed, zero failed;
- module-qualified command mutation: six executed with only the AST ownership
  test failing;
- cleanup-report mutation: six executed with only the typed-report test
  failing;
- full Architecture project: 312 passed, zero failed;
- review-invariant corpus: 11 invariants, seven projects, 322 tests and five
  effective adversarial proofs;
- one-assembly Local `-ValidateForensics`: nine phase results, zero failures
  and ownership 619/0. The report retained `ExecutionFailed` plus
  `TerminateFailed/UnauthorizedAccessException` and
  `ReapDeadlineExceeded/TimeoutException`;
- PowerShell parsing, 22 tracked valid JSON files,
  `dotnet format --verify-no-changes` and `git diff --check`: pass.

The first Windows routed run reached the final project after the preceding
seven projects passed, then one unchanged process-lease test hit a transient
sharing violation while opening an atomically published temporary JSON probe.
The complete owning test class subsequently passed 27/27. The full routed run
was not repeated to turn that diagnostic into a green result; exact-head
Windows CI remains the authoritative closure gate.

Required native CI and a new clean Codex review must now converge on the final
documentation head. Review `5048688459` is not acceptance evidence because it
contains the two findings above.

## Final-Head Assembly Lifecycle Blocker

The documentation checkpoint at `dd77ebe2e1f19b2a2e3c41ffa56d578f427cf26b`
had one required failure before formal lifecycle phases: the capture-window
self-test reported only `did not fail closed`. Its failed artifact retained the
ownership audit but not the structured self-test result. The preceding green
implementation head and this documentation head had identical runtime trees;
the intervening four files were Markdown only, and both GitHub merge commits
used the same base and runner image.

State-machine inspection and executable reproduction classify the failure as a
self-test/hosted-startup publication race. The one-second absolute child window
started before supervisor launch, containment, pipe handshakes and target
start, while `--block-forever` published no proof that target code had reached
the intended blocking transition. Exhausting the window before
`TargetStarted` is a legal collector result:
`OperationDeadlineExceeded` with `Started=false`. It must not satisfy a proof
that is specifically about post-start timeout, terminate, reap and drain. The
old failed artifact cannot identify which derived Boolean was false because the
generic throw preceded report writing; the one-millisecond executable mutation
reproduces the same pre-ready state and rejection deterministically.

Implementation commit `d698f855afec7e2731b29d1643f473b201096207`
corrects only the fixture, behavioral oracle and failure observability:

1. The fixture establishes its non-completing blocking task before publishing
   a typed ready record.
2. Post-start timeout tests share that fixture and use a three-second
   owner-assigned startup window. Production collector, lease, cleanup and
   global deadlines are unchanged.
3. The PowerShell oracle requires ready PID, blocking-task state, stdout/stderr
   markers, `OperationDeadlineExceeded`, `Started`, reap, drain, empty cleanup,
   bounded elapsed time and remaining parent budget.
4. The structured capture self-test is written to a standalone JSON artifact
   and job output before the phase-level throw.
5. Executable startup-window and early-ready mutations each fail only their
   owning Architecture proof and are registered in the review-invariant
   corpus.

Local proof for the implementation commit produced:

- the startup-window mutation: expected rejection with
  `OperationDeadlineExceeded`, `Started=false` and no ready record;
- the early-ready mutation: expected rejection with typed timeout,
  reap/drain complete and `BlockingTaskEstablished=false`;
- focused collector platform tests: 16 passed, zero failed;
- focused lifecycle Architecture tests: eight passed, zero failed; each new
  mutation executed eight tests and failed only its owning assertion;
- one-assembly Local `-ValidateForensics`: one assembly, nine phase results,
  zero failures, typed timeout, ready/blocking proof, reap/drain complete and
  zero cleanup failures;
- review-invariant corpus: 11 invariants, seven projects, 324 tests and seven
  executable adversarial proofs;
- full Architecture project: 315 passed, zero failed;
- Windows routed suite: an earlier implementation run passed 72/72. The final
  run passed 71/72 and hit the existing atomically-published probe sharing
  violation in unchanged
  `CallerCancellationStillCompletesOwnedCleanupBeforePropagating`; the owning
  lease class then passed 27/27 and the full suite was not retried merely to
  obtain green output;
- `git diff --check` and `dotnet format --verify-no-changes`: pass.

Exact-head native CI, Assembly Lifecycle and a clean same-head Codex review
remain required. This correction does not reopen Stage 3 collector semantics,
Stage 4A feasibility or any production restart work.

## Native CI Matrix

### Required Jobs And Gates

- Windows x64 process-supervision tests;
- Linux tests inside the existing delegated cgroup environment;
- macOS x64 process-supervision tests;
- macOS arm64 process-supervision tests;
- Assembly Lifecycle Stability with forensics validation;
- Strict PR CI;
- focused and full Architecture tests;
- exact-head Codex review after every required check converges.

### Platform-Neutral Behavior

Immutable launch snapshots, normal/nonzero exit, cancellation taxonomy, timeout
taxonomy, empty cleanup lists, primary-plus-cleanup aggregation and bounded
large-output drain use ordinary process capabilities and must pass on every OS.

### Platform-Native Behavior

Terminate/reap injection, inherited-stream retention, parent exit with a live
descendant, owned-set quiescence and absence of a residual collector require the
actual Windows, Linux and macOS process backends. Linux proof runs inside the
existing delegated environment because collector ownership may indirectly
compose the process lease there.

This checkpoint does not acquire or redesign the delegated scope. Delegation is
an execution-bootstrap precondition shared by lifecycle and test runners. A
delegation failure remains fail closed and is not replaced by PID/PPID polling.

## Commit And Closure Sequence

1. Create one implementation commit containing the typed core, caller migration,
   deletion of the replaced owner, architecture guard and required tests. Phase
   1 and Phase 2 are validation milestones inside this one reviewable
   implementation checkpoint; do not publish an exact-head closure with both
   owners active.
2. Record the non-self-referential implementation commit SHA.
3. Create a separate documentation/checkpoint commit that records that SHA and
   actual validation evidence.
4. Push one exact HEAD to the existing PR #197 branch.
5. Wait for every required native and shared CI gate to converge on that HEAD.
6. Request and complete a same-head Codex review.
7. Record closure evidence without rewriting the existing Stage 3 closure.
8. Confirm local, origin and PR HEAD equality and a clean working tree.

Do not merge PR #197 and do not move or recreate a release tag.

## Stop Rules

Stop this checkpoint and report the violated assumption if implementation proves
any of the following:

- the Stage 2 `OwnedProcessLease` contract does not hold;
- the collector requires target identity, containment, membership or quiescence
  authority;
- lifecycle capture policy cannot allocate and attenuate the collector window;
- completion requires Stage 4 restart or Stage 5 central-runner changes;
- implementation requires another independent deadline owner;
- a native platform invalidates the proposed start/wait/terminate/reap/drain
  semantics;
- composition requires weakening the target lease or adding PID/PPID
  correctness.

Do not mask these conditions with retries, sleeps, a larger global timeout,
polling fallback, stdout filtering or PID/PPID process truth.

## Rollback Boundary

Rollback is a whole collector-checkpoint revert: contracts, implementation,
tests, PowerShell caller migration, legacy deletion, architecture gate and
documentation return together to the last closed exact HEAD.

The completed checkpoint must never retain this permanent configuration:

```text
PowerShell collector owner
+
C# collector owner
```

There is one active collector lifecycle owner. A failed migration is reverted as
one checkpoint rather than preserved behind a fallback switch.

## Completion Criteria

The checkpoint is complete only when:

1. PowerShell no longer starts, waits, terminates, reaps or drains a diagnostic
   collector child.
2. PowerShell does not create or renew the collector deadline or aggregate its
   failures.
3. Typed outcomes distinguish normal nonzero exit from lifecycle failure.
4. Primary and cleanup failures are preserved without `null` collection shape.
5. No raw collector process or cleanup target crosses into PowerShell.
6. The same process behavior and mutations pass on the required native matrix.
7. No fallback path remains.
8. Required CI and same-head review converge on one exact HEAD.
9. PR #197 remains open and unmerged unless the owner separately authorizes a
   later merge.
