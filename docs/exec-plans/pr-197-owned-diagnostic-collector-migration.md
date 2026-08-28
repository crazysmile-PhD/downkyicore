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

## Formal-Phase Attach/Shutdown Diagnostic Checkpoint

The next exact-head Strict PR run
[33165929460](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33165929460),
Assembly Lifecycle job
[98831200568](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33165929460/job/98831200568),
passed 19 applicable checks, skipped nine release-only checks and failed only
the formal lifecycle phase. Artifact `assembly-lifecycle-pull_request-1`
(`9683800393`, downloaded SHA-256
`3f643a1ef512dbc4054aacc296e0c59f8ae8d9896388991984aa08a40c02d0db`)
identifies `DownKyi.Core.Tests` iteration 2 execution, PID `8852`:

- target marker `started` was `2026-08-28T11:12:03.763Z`, `disposing` was
  `11:12:07.771Z`, `disposed` was `11:12:07.772Z`, and the reported
  post-dispose exit interval was 29 ms;
- actual target lifetime was therefore approximately 4.038 seconds, below the
  unchanged five-second slow threshold;
- the capture lead armed at approximately the teardown boundary and the pinned
  `dotnet-stack 9.0.661903` invocation was
  `dotnet-stack report --process-id 8852`;
- the collector consumed 15,019.085 ms with empty stdout/stderr, returned typed
  `OperationDeadlineExceeded`, and still reported started, exited, reaped and
  streams drained with an empty cleanup list;
- the synchronous observer inflated the old phase stopwatch to 19,209.895 ms,
  which then produced `SlowEvidenceMissing` even though target exit had already
  completed.

Classification is A plus D: the diagnostics attach handshake began while the
target entered CLR/test-runner shutdown. This is not stack enumeration,
publication, stream drain, reap or generic hosted-runner slowness. In pinned
source the report command starts the EventPipe session before trace-file
creation or output. The historical artifact cannot observe that internal call,
but its teardown ordering and empty streams match deterministic exact-tool
proof:

1. A Windows diagnostics-pipe emulator published listening before tool launch,
   accepted the exact tool connection at 460.774 ms and deliberately withheld
   the protocol reply. No `.nettrace` or stream byte appeared. The three-second
   window returned typed `OperationDeadlineExceeded` at 3,001.445 ms, reap
   completed at 2,996.152 ms, streams drained at 2,999.204 ms, cleanup was empty
   and 6,498.104 ms of parent operation budget remained.
2. With a real diagnostics-ready fixture, `.nettrace` appeared at 361.129 ms.
   Exiting the target at 382.475 ms made the collector return at 484.503 ms with
   `ServerNotAvailableException` during session stop. An established session
   therefore does not reproduce the 15-second stall.
3. The owner-visible timeline now records all 11 requested transitions and
   marks tool-internal attach/capture as `NotObservable`, rather than inventing
   a diagnostic authority.

Implementation commit `35606b5cdbd7a011b7a515fd7a6aa28c8c4f9039`
is the minimum fix. The target lease exposes only a read-only exit token and its
monotonic exit offset. Lifecycle policy links target exit to observer
cancellation, so an already-started diagnostic child returns typed
`CallerCancelled` and completes bounded cleanup when its target is gone. Phase
duration uses the owner's target-exit offset instead of observer wall time. The
unlinked mutation consumes the full three-second collector allowance and returns
`OperationDeadlineExceeded`. The 15-second capture window, five-second cleanup
allowance, parent `TransitionBudget`, collector ownership/reap/drain semantics
and absence of retry/sleep are unchanged.

Local proof for that implementation commit produced:

- focused collector/window tests: 19 passed, zero failed;
- affected lifecycle, ownership and IPC Architecture tests: 19 passed, zero
  failed;
- one-assembly Local `-ValidateForensics`: one assembly, nine phase results,
  zero failures, one slow phase captured, zero missing evidence and lifecycle
  ownership 633/0; the attach-stall JSON retained the 11-transition evidence;
- full Architecture project: 316 passed, zero failed;
- review-invariant corpus: 11 invariants, seven projects, 325 tests and seven
  executable adversarial proofs;
- routed Windows solution: eight projects, 1,132 total, 1,130 passed, one
  skipped and one failed. The only failure was the unchanged atomic-file
  sharing violation in
  `CallerCancellationStillCompletesOwnedCleanupBeforePropagating`; the routed
  suite was not rerun merely to obtain green output, while the affected
  collector class passed 19/19;
- PowerShell parsing, `git diff --check` and
  `dotnet format --verify-no-changes`: pass.

Status: pending the documentation checkpoint, exact-head push, required native
and Strict PR CI, Assembly Lifecycle and a clean same-head Codex review. Stage
4A remains completed and closed; production Stage 4, Stage 5, Stage 6, merge,
release and tag movement remain out of scope. No stop rule was triggered and no
closed Stage 3 single-deadline, authoritative reap or drain contract was
reopened.

### First Exact-Head CI And Proof-Fixture Follow-up

Documentation head `0c888fa76fcbd7c160c2fb4405f990430b125b2e`
triggered Strict PR run
[33171141030](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33171141030)
without rerunning the earlier failure. Eighteen checks passed, nine release-only
checks skipped and two checks failed. The artifacts proved the production
diagnostic correction itself:

- Assembly Lifecycle ran eight assemblies and 147 phase results. All 12 slow
  phases captured evidence, `SlowEvidenceMissing` was zero, the real attach-stall
  self-test passed and lifecycle ownership remained 633/0.
- Its only execution failure was the new unlinked-cancellation mutation in
  `DownKyi.Windows.Tests`. The 600 ms test allowance expired before hosted
  process startup published the blocking-ready record, so the mutation failed
  its own synchronization precondition rather than the target-exit contract.
- The Windows review-invariant job passed the first two lifecycle adversarial
  profiles. The capture-budget profile then reached the outer wrapper without
  the exact rejection. The old wrapper returned `null` for a host-execution or
  temporary-directory cleanup exception without retaining its type. Source
  inspection showed that `Directory.Delete` in `finally` could replace an
  already-captured child result, while local Windows validation in this
  checkpoint had already exposed transient sharing violations. The corpus
  correctly failed closed with zero owning failures instead of accepting an
  unrelated exception as proof.

Test-only implementation follow-up
`63cbd5d2e310ebc28330999f153cadf27a334552` preserves the proof boundaries:

1. The unlinked target-exit mutation uses the already-established three-second
   hosted-start fixture allowance, waits on the published blocking transition,
   proves target exit occurs before the allowance and then proves the collector
   consumes at least 2.5 seconds before typed timeout.
2. Mutation report cleanup remains best effort. A sharing or access failure is
   emitted as xUnit diagnostic output and cannot replace the already-captured
   child exit/output. Only the exact lifecycle self-test rejection makes the
   adversarial profile red.

No retry, sleep, deadline increase, policy change, collector production change
or ownership change was added. Local follow-up validation passed the focused
collector/window classes 19/19, the normal lifecycle Architecture class 9/9,
the capture-budget mutation with exactly one owning failure among nine tests,
the full Architecture project 316/316, the review corpus with 325 tests and all
seven adversarial proofs, and the routed Windows project 74/74. Format and diff
checks remained clean.

Status: pending this follow-up documentation checkpoint, a new exact-head push,
required CI and same-head review. The first exact-head run is diagnostic
evidence, not a run to rerun or relabel green.

### Second Exact-Head CI And Attach-Oracle Correction

Documentation head `1fd49889ffdbd11d3534e36fcd9e8c15e1e4e4b2`
triggered Strict PR run
[33173057693](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33173057693).
Eighteen checks passed, nine release-only checks skipped and two checks failed.
Neither failure invalidated the formal-phase diagnosis or the target-exit fix:

- Assembly Lifecycle job
  [98854688529](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33173057693/job/98854688529)
  failed before formal phases in the attach-stall self-test. The exact fake
  diagnostics target accepted the connection, the collector returned typed
  `OperationDeadlineExceeded` at 2,999.099 ms with empty output and no trace,
  reap completed at 2,992.910 ms, drain completed at 2,994.918 ms, cleanup was
  empty and 6,661.646 ms of parent budget remained. Only
  `windowConsumedAfterStart` was false because it incorrectly required 2.5
  seconds after the external tool's `ProcessStarted` transition. The
  caller-owned three-second window already includes tool startup.
- Windows review job
  [98854689514](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33173057693/job/98854689514)
  again failed closed when the capture-budget adversarial profile produced no
  exact owning failure. The wrapper's passing path did not retain the caught
  host or cleanup exception type in CI output, so this remains a proof-host
  observability issue rather than accepted mutation evidence.

Test-only implementation follow-up
`13207a0d4e546068367279bea8932aea8c292ac7` corrects only those proof oracles.
The diagnostics-pipe fixture now publishes a connection UTC timestamp, and the
lifecycle self-test records collector request/typed-return UTC timestamps. It
requires pipe acceptance inside that interval and at least 2,900 ms from
request to typed timeout; it no longer compares monotonic durations from two
different processes. The mutation wrapper emits the exact caught exception
type to xUnit test output without changing pass/fail classification. No
production code, timeout, retry, sleep, lease, collector owner, cleanup policy
or Stage 4A boundary changed.

Exact local attach proof recorded request `1787922077742`, connection
`1787922078276` and typed return `1787922080754` Unix milliseconds. Collector
process start was 287.756 ms and typed return was 2,992.776 ms on the collector
timeline; pipe acceptance was inside the request/return interval, no progress
appeared, reap/drain completed and 6,302.324 ms of parent operation budget
remained. The one-assembly lifecycle gate then reported nine phases, zero
failures, zero missing slow evidence and a passing attach-stall self-test.

Local final validation for this implementation head passed focused collector
tests 19/19, the normal lifecycle Architecture class 9/9, the capture-budget
mutation with eight passes and exactly one owning failure among nine executed
tests, full Architecture 316/316, Windows 74/74, and the review corpus with 325
tests plus all seven adversarial proofs. Process-supervision build, PowerShell
parse, format verification and diff checks also passed.

Status: pending this documentation checkpoint, exact-head push, required native
and Strict PR CI, and a same-head Codex review. The failed exact-head run is
retained as diagnostic evidence and will not be rerun. Stage 4A remains
completed and closed; Stage 4 production, Stage 5, merge, release and tag work
remain out of scope.

### Third Exact-Head CI And Interrupted-Stack Retention

Documentation head `9aabab02c713a2ff3bd7b6c469a19e6298e4a48c`
triggered Strict PR run
[33174514303](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33174514303).
Ten jobs passed and two failed. The run was allowed to finish and was not
rerun:

- Windows job
  [98859549607](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33174514303/job/98859549607)
  again stopped on the capture-budget adversarial profile with nine executed
  and zero failed tests. The uploaded TRX proved that the owning test passed,
  but the reporter omits passing test output. Test-output diagnostics therefore
  did not make the nested child outcome observable.
- Assembly Lifecycle job
  [98859549606](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33174514303/job/98859549606)
  completed all eight assemblies and 147 phase results. The corrected
  attach-stall self-test passed: connection acceptance occurred inside the
  request/return interval, typed timeout was 3,000.436 ms, no tool progress or
  trace appeared, reap/drain completed, cleanup was empty and 6,717.574 ms of
  parent operation budget remained. Ten of 12 slow phases captured evidence.
  The two remaining rows were genuine slow targets, not observer-inflated
  duration:
  - `DownKyi.Core.Tests` iteration 1 exited at 6,153.404 ms. Its collector was
    canceled after 2,008.061 ms with 7,866 stack characters, first stack output
    at 980.976 ms, typed return at 1,944.391 ms, reap/drain complete and no
    cleanup failure.
  - `DownKyi.Tests` iteration 1 exited at 5,210.819 ms. Its collector was
    canceled after 1,054.423 ms with 15,008 stack characters, first stack
    output at 998.821 ms, typed return at 1,004.106 ms, reap/drain complete and
    no cleanup failure.

Both rows were mislabeled `SlowEvidenceMissing` even though their typed
`CallerCancelled` evidence contained concrete `Thread (0x...):` stack output.
The lifecycle failure path discarded those already-drained bytes solely because
target exit canceled the observer after output began.

Implementation follow-up `07b076a7ef69e75e831c84ef9b5b12f8d066fb8f`
retains that interrupted stack through the existing evidence directory and
marks the capture complete only under a narrow typed predicate:
`CallerCancelled`, no cleanup failure, collector started/exited/reaped/drained,
not timed out, observed `StackOutputFirstByte`, and pinned-tool thread output.
It preserves the failure kind, timeline and cleanup list in the phase report.
Empty cancellation output and unrelated failure kinds remain rejected by the
new structured self-test. The mutation wrapper now uses xUnit warnings only
for unexpected child results or host/cleanup failures, so that passing-test
diagnostics are embedded in TRX without making an unrelated failure count as
the owning adversarial proof.

Local validation passed one-assembly lifecycle with nine phases, zero failures,
one slow phase captured, zero missing evidence, lifecycle ownership 633/0, a
passing attach-stall self-test and all three interrupted-stack predicates. The
normal lifecycle Architecture class passed 9/9; the capture-budget mutation
executed nine tests and failed exactly its one owning test; full Architecture
passed 316/316; the review corpus passed 325 tests and seven adversarial proofs;
PowerShell parse, format and diff checks passed. The unchanged collector and
Windows platform classes were not rerun after this script-only follow-up; their
latest direct results remain 19/19 and 74/74 from the preceding implementation
checkpoint.

Status: pending this documentation checkpoint, exact-head push, required CI and
same-head review. No timeout, slow threshold, retry, sleep, collector owner,
target lease, production path, workflow or central-runner policy changed.
Stage 4A remains completed and closed; Stage 4 production, Stage 5, merge,
release and tag movement remain out of scope.

### Fourth Exact-Head CI And Caller-Boundary Oracle

Documentation head `1e8caf0c3671550d7c8e34e16322b95aee68b5f7` triggered Strict
PR run
[33176593640](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33176593640).
Ten of 12 jobs passed; the two failures were allowed to finish and were not
rerun:

- Assembly Lifecycle job
  [98866705631](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33176593640/job/98866705631)
  failed before formal phases only because the attach-stall proof used the
  compiled collector's later monotonic origin. Artifact `9687993595` records an
  outer request at `1787924804412` and typed return at `1787924807418`, exactly
  3,006 ms apart, while the internal `TypedOutcomeReturned` transition was
  2,844.331 ms. The connection was accepted inside the outer interval, the
  result was typed `OperationDeadlineExceeded`, output and trace were empty,
  reap/drain completed, cleanup was empty and 6,772.763 ms of parent budget
  remained. Only `windowConsumedWithoutProgress` was false.
- Windows job
  [98866705865](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33176593640/job/98866705865)
  failed closed when the capture-budget adversarial profile executed nine tests
  with zero failures. Artifact `9688059467` exposes the nested result: the
  Windows runner did not install `dotnet-stack`, so the lifecycle child stopped
  on that unrelated precondition before reaching the mutation. The owning test
  correctly refused to count that failure, and the outer corpus correctly
  rejected the 9/9 profile.

Test-only implementation follow-up
`155df31624d732b72c010d52c89a01f5287fd209` measures the attach-stall predicate
from the already-recorded outer request/typed-return UTC boundary. It also lets
the independent capture-window fixture run before checking the real-tool
dependency and places the exact owning rejection in TRX. The normal Windows
path still fails closed on a missing pinned tool before the attach proof or
formal capture begins. No timeout, retry, sleep, collector/lease authority,
production code, workflow or Stage 4A state changed.

Exact implementation-head local validation reported one assembly, nine phases,
zero failures, one slow phase captured, zero missing evidence and lifecycle
ownership 633/0. The attach fixture accepted the connection between caller
request `1787925598017` and typed return `1787925601026`, a 3,009 ms interval;
the typed transition was 2,999.726 ms and 6,606.488 ms of parent operation
budget remained. The normal lifecycle Architecture class passed 9/9; the
capture-budget mutation executed nine and failed exactly one with the explicit
owning rejection; full Architecture passed 316/316; the review corpus passed
325 tests and seven adversarial proofs; PowerShell parse, format and diff checks
passed.

Status: pending exact-head push, required CI and same-head review. The four
failed exact-head runs remain diagnostic evidence and will not be rerun. Stage
4A remains completed and closed; production Stage 4, Stage 5, merge, release
and tag movement remain out of scope.

### Fifth Exact-Head CI And Started-Collector Fixture

Documentation head `e7842f5a155afb5101cb42ef545204d2976c528d` triggered Strict PR
run
[33178177118](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33178177118).
All other exact-head workflows passed, and 11 of 12 Strict jobs passed. The run
was allowed to finish and was not rerun:

- Assembly Lifecycle job
  [98872168523](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33178177118/job/98872168523)
  passed all eight assemblies and 147 phases. Artifact `9688857106` records six
  slow phases, six captured evidence bundles, zero missing evidence, ownership
  633/0 and passing interrupted-stack and attach-stall self-tests. The attach
  caller interval was 3,020 ms, with accepted diagnostics connection, typed
  deadline, empty tool output/trace, authoritative reap/drain and no cleanup
  failure.
- Windows job
  [98872168535](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33178177118/job/98872168535)
  passed Architecture and the complete review corpus, including the exact
  capture-budget mutation, then failed one of 74 full-project tests. Artifact
  `9688787788` identifies only
  `TargetExitCancellationStopsAnAlreadyStartedCollector`: its result was typed
  `CallerCancelled`, but `Evidence.Started` was false.

The failure was a deterministic-fixture ordering gap. The target watched the
same file that the collector child published after establishing its blocking
task. The child could publish and make the target exit before the collector
owner returned from lease acquisition and recorded `ProcessStarted`; therefore
the test name's already-started precondition was not established. Fixture
follow-up `f38cd23bf2f8acc3978d11adcedbace3d236e2e8` separates collector-ready
from target-exit signaling and adds a nonthrowing internal test-only completion
source set immediately after the owner records `ProcessStarted`. The test waits
for both that observation and the blocking-ready record before signaling target
exit. The unlinked mutation keeps the same order and still expects typed
`OperationDeadlineExceeded`.

Exact implementation-head local validation passed the collector platform class
18/18 and the full Windows project 74/74. Full Architecture passed 316/316 and
the review corpus passed 325 tests plus seven adversarial proofs on the same
fixture behavior; format and diff checks passed after the final completion-source
narrowing. The public collector API, lifecycle caller, timeout, retry, sleep,
owner, lease, cleanup, production workflow and Stage 4A state are unchanged.

Status: pending exact-head push, required CI and same-head review. The five
failed exact-head runs remain diagnostic evidence and will not be rerun. Stage
4A remains completed and closed; production Stage 4, Stage 5, merge, release
and tag movement remain out of scope.

### Sixth Exact-Head CI And Slow-Evidence Startup Ordering

Documentation head `b83f321b6d71a2343aa234257bcfc641dea6595b` triggered Strict PR
run
[33180214921](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33180214921).
Eleven of twelve Strict jobs passed, including the complete Windows job
[98879255851](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33180214921/job/98879255851).
The separately triggered Build, CodeQL, Process Membership and Protobuf
workflows also passed. The run was allowed to finish and was not rerun.

Assembly Lifecycle job
[98879255304](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33180214921/job/98879255304)
failed one of 147 phases. Artifact `9689688869` records eight assemblies, 12
slow phases, 11 captured evidence bundles, one missing bundle, ownership 634/0
and passing attach-stall, interrupted-stack and capture-window proofs. The only
failed row was `DownKyi.Infrastructure.Tests`, iteration 3, `execution`:

- the authoritative target exited successfully and quiescently at 5,374.329
  ms; no process or cleanup failure occurred;
- the slow-evidence attempt occupied 1,248.416 ms and returned typed
  `CallerCancelled`, not `OperationDeadlineExceeded`;
- request creation and process-start request were observed at 0 and 0.010 ms,
  but `ProcessStarted`, attach, progress, stack output, exit, reap and drain were
  all not observed; typed return was 1,199.008 ms and both streams were empty;
- the configured one-second lead armed the request before the five-second
  threshold, but hosted launch and ownership establishment had not completed
  when target exit cancelled the observer.

This classifies the residual failure as hosted collector-startup ordering, not
stack enumeration, publication, stream drain or exhaustion of the 15-second
capture window. The fail-closed `SlowEvidenceMissing` result was correct because
there was no stack to salvage. Accepting pre-start cancellation as evidence
would weaken the existing slow-phase invariant.

Test-policy implementation
`63d53cbea6731bf1f0896bf66a4d9b02aa0c5d4c` uses the already-established
three-second hosted collector startup allowance as the slow-evidence lead. It
therefore issues the request two seconds into a five-second phase instead of
four seconds in, while leaving the five-second slow threshold, 15-second
capture window, five-second cleanup allowance, 180-second phase timeout,
parent `TransitionBudget`, collector implementation and owner semantics
unchanged. No retry, sleep, new deadline owner or empty-evidence acceptance was
added.

The executable ordering proof models a 1,500 ms hosted collector startup and a
5,100 ms qualifying target: the configured lead reaches collector start before
target exit, while the former one-second lead does not. Mutating the shared
allowance back to 1,000 ms executed ten affected Architecture tests and failed
exactly that proof (9 passed, 1 failed). On clean implementation HEAD, focused
collector tests passed 18/18, affected Architecture passed 10/10, full
Architecture passed 317/317, Windows passed 74/74 and the review-invariant gate
passed 326 tests plus seven adversarial proofs. One-assembly Local
`-ValidateForensics` recorded the exact implementation SHA, clean worktree, one
assembly, nine phases, zero failures, ownership 634/0, lead 3,000 ms and capture
window 15,000 ms; capture-window, interrupted-stack, attach-stall and
pre-threshold proofs all passed. That local execution produced no naturally
slow formal phase, so the hosted artifact remains the direct reproduction of
the startup race.

Status: pending documentation commit, exact-head push, naturally triggered CI
and same-head review. The six failed exact-head runs remain diagnostic evidence
and will not be rerun. Stage 4A remains completed and closed; production Stage
4, Stage 5, merge, release and tag movement remain out of scope.

### Seventh Exact-Head CI And Behavioral Ordering Proof

Documentation head `d7f6f4b9d52d59e1c68321c1c6d46b073b5ae7db` triggered Strict PR
run
[33183210973](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33183210973),
which passed all 12 jobs. Build
[33183211226](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33183211226),
CodeQL
[33183210974](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33183210974),
Process Membership
[33183210976](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33183210976)
and Protobuf
[33183211018](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33183211018)
also passed on the same PR head without rerunning an old workflow.

Assembly Lifecycle artifact `9690837165` records eight assemblies, 147 phases,
zero failures, eight slow phases, eight captured evidence bundles, zero missing
bundles, lead 3,000 ms, capture window 15,000 ms, ownership 634/0 and all
forensics self-tests passing. Two near-threshold formal rows at 5,040.65 and
5,523.58 ms both captured evidence. The artifact's checkout SHA is GitHub's
synthetic pull-request merge SHA `8f0a5a51214e77cf544796a926433be96477ef42`;
the workflow and PR head remained `d7f6f4b9d52d59e1c68321c1c6d46b073b5ae7db`.

The first all-green same-head Codex review
[5052599699](https://github.com/crazysmile-PhD/downkyicore/pull/197#pullrequestreview-5052599699)
reported two P2 findings in existing Linux delegation and central test-runner
cleanup paths. Those are explicitly excluded by this checkpoint's Linux
delegation and Stage 5/central-runner boundaries and did not invalidate the
slow-evidence change. Scoped same-head review
[5052661384](https://github.com/crazysmile-PhD/downkyicore/pull/197#pullrequestreview-5052661384)
reported two relevant P1 proof defects:

1. the 1.25-second real-stack self-test threshold became zero-clamped under the
   three-second lead, so it no longer proved a positive post-lease arming delay;
2. the Architecture mutation evaluated detached arithmetic instead of executing
   the real `Invoke-IsolatedProcess` slow-evidence path.

Test implementation `d3bd6c152cc1296a7cdc0975b10a1ff7d87ff771`
closes both findings. The real-stack evidence-hold fixture now uses a
4.25-second synthetic threshold and proves an observed positive 1.25-second
capture threshold. The clamp uses the double `Math.Max` overload so fractional
thresholds are not truncated. A new internal delayed-exit target publishes
typed ready evidence, then runs a bounded eight-second asynchronous workload.
The lifecycle self-test passes that target through the real slow-evidence path
with a five-second synthetic observer-start delay: the configured lead arms at
two seconds and captures evidence, while a one-second lead mutation arms at four
seconds and must fail as `SlowEvidenceMissing` after target-exit cancellation.
The synthetic ordering proof skips the managed-stack tool only after the
ordering boundary; the separate real-stack fixture remains mandatory.

On clean implementation HEAD, the configured path captured evidence in
5,487.245 ms before target exit at 8,275.223 ms. The mutation returned
`capture-failed` in 4,061.31 ms and `SlowEvidenceMissing` when its target exited
at 8,283.544 ms. Both paths published the expected target PID/delay state,
remained authoritatively quiescent and had zero cleanup failures. One-assembly
Local `-ValidateForensics` recorded the exact SHA, clean worktree, one assembly,
nine phases, zero failures, ownership 634/0, observed positive threshold 1.25
seconds, lead 3,000 ms and capture window 15,000 ms. Focused collector tests
passed 18/18, affected Architecture 9/9, full Architecture 316/316, Windows
74/74 and the review-invariant gate 325 tests plus seven adversarial proofs.
PowerShell parse, C# format and diff checks passed. No collector production
code, retry, observer deadline, parent budget, phase timeout, workflow or Stage
4/5 state changed.

Status: pending documentation commit, exact-head push, naturally triggered CI
and a new same-head review. The six failed exact-head runs remain diagnostic
evidence and will not be rerun. Stage 4A remains completed and closed;
production Stage 4, Stage 5, merge, release and tag movement remain out of
scope.

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
