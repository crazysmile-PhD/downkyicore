# Assembly Lifecycle Stability Gate

Status: required quality and release gate

## Purpose

A green test summary does not prove that a test executable loaded cleanly,
preserved its runner protocol, disposed assembly fixtures, stopped foreground
threads or exited deterministically. This gate treats each xUnit test assembly
as a real process and measures all lifecycle boundaries separately.

The first incident covered by this policy occurred when
`TestDataIsolation.ProcessExit` ran synchronous recursive cleanup after xUnit
had returned from its runner. xUnit's foreground-thread watchdog then wrote
`Waiting 10 seconds for foreground threads to exit` after the valid
`-assemblyInfo` JSON. The process returned exit code 0, but the Visual Studio
adapter could no longer parse stdout as one JSON object.

The permanent correction is:

- test data isolation is an xUnit assembly fixture implementing
  `IAsyncDisposable`;
- fixture cleanup emits a private lifecycle marker and does not register
  `ModuleInitializer` or `ProcessExit`;
- Desktop tests use `Avalonia.Headless.XUnit` per-assembly isolation instead of
  a custom process-lifetime dispatcher thread;
- `DesktopApplication.RunAsync` awaits `App.DisposeAsync` after the Avalonia
  main loop, and application disposal requests Host shutdown before releasing
  resources;
- `SqliteDownloadTaskStore.Dispose` clears only its owned connection pool;
- the system benchmark dispatcher uses a bounded join.

The regular solution and review-invariant gates additionally execute
`DownKyi.Desktop.Tests` through the xUnit in-process executable declared in
`test-runner-policy.json`. This avoids the VSTest adapter's hidden
`-assemblyInfo` parse race tracked by `xunit/xunit#3576`; xUnit 4 prereleases
cannot be used yet because `Avalonia.Headless.XUnit` 12.1.0 targets the xUnit 3
discovery API. This is test-host routing only. The lifecycle gate below still
executes and validates `-assemblyInfo`, discovery, execution, teardown and
process exit as separate fail-closed phases.

## Dynamic Phases

`script/test-assembly-lifecycle.ps1` discovers every `*.Tests.csproj`, validates
its explicit `Windows` / `Linux` / `macOS` ownership set, and probes only
assemblies whose declaration includes the current OS. It runs each phase in an
independent child process:

1. `load`: a collectible `AssemblyLoadContext` loads the assembly, runs its
   module constructor, unloads it and proves the context is no longer rooted.
2. `assembly-info`: the xUnit executable runs `-assemblyInfo`; stdout must be
   exactly one JSON object.
3. `discovery`: the xUnit executable lists tests in automated mode; stdout must
   be exactly one JSON array.
4. `execution`: the xUnit executable runs tests without inter-assembly process
   reuse; every non-empty stdout line must be valid JSON.
5. `assembly-teardown`: the xUnit assembly fixture must emit
   `started -> disposing -> disposed`, and its process-specific data root must
   be absent.
6. `process-exit`: the process must exit within the configured post-teardown
   deadline without residual children or runner-protocol pollution.

The lifecycle gate uses xUnit's synchronous automated reporting mode
(`-automated sync`) for assembly-info, discovery and execution. This is not a
diagnostic suppression: stdout remains machine-readable JSON, stderr is still
captured, and the same timeout, slow-phase and process-exit checks remain
active. The setting prevents the gate itself from creating xUnit's
asynchronous `MessageBus` reporter foreground thread, so any future
foreground-thread watchdog must come from the tested assembly or another
explicit owner. Because xUnit's synchronous reporter normally waits for a
carriage return after each report, every isolated child has redirected stdin
closed immediately after launch. The reporter therefore observes deterministic
EOF instead of depending on whether the gate was launched from an interactive
terminal.

The three xUnit phases share one guarded invocation path. It rejects any
reporter arguments other than exactly one `-automated sync` pair before process
launch. A runtime mutation self-test deliberately substitutes `async` and must
be rejected by the same validator; the machine report records the result as
`reporterContractSelfTestPassed`.

This is the verified engineering mitigation for the lifecycle gate. It does
not claim that the final direct blocker in the historical intermittent
specimen was captured or proven with complete forensic certainty.

Every phase records its exit code, duration, timeout state, stdout/stderr
protocol state, typed process ownership and diagnostic observations. Job active
process state, Linux cgroup v2 membership or macOS libproc group membership is
the residual-process truth. PID, parent PID, process name, creation time, tree
depth and a redacted command line remain diagnostic snapshot fields only. The
report aggregates P50, P95, P99 and maximum duration per assembly and phase.
Schema 4 records `processFailureType` and `forensicsFailureType` separately in
addition to the general `failureType` and `errorType`; slow, exit and residual
evidence errors cannot overwrite the process owner's causal failure.

### Measurement Definitions

- `load`, `assembly-info`, `discovery` and `execution` duration starts
  immediately before child-process creation and stops when the OS reports that
  child exited. `execution` therefore includes runner startup, test methods,
  assembly fixture teardown and CLR/runner shutdown.
- `assembly-teardown` is the fixture marker interval from `disposing` to
  `disposed`. Marker timestamps use Unix milliseconds, so this metric has
  millisecond resolution.
- `process-exit` is the interval from the fixture `disposed` marker to the
  child's OS `Process.ExitTime`. Report serialization, stdout/stderr copying and
  process-tree inspection are not part of this metric.
- The slow classification remains exactly
  `duration >= slowPhaseThresholdSeconds`. To prevent runner scheduling from
  crossing directly from just below the threshold to an already-exited child,
  evidence capture is armed 1,000 ms early. The report records this as
  `slowEvidenceCaptureLeadMilliseconds` and per phase as
  `slowEvidenceTriggeredBeforeThreshold`; it does not lower the slow
  threshold. `slowEvidenceStatus` is `captured`, `capture-failed`, or
  `process-exited-before-capture`; the latter two still fail a slow phase
  instead of leaving an unexplained empty evidence array.
- `-ValidateForensics` asks `OwnedProcessLease` for an evidence-hold sub-state to
  prove the capture lead actually ran before a synthetic 1.25-second threshold.
  The lease creates and owns the one-shot endpoint before target execution; the
  observer only returns `Captured` or `Failed`, the actual held target must
  acknowledge the handoff after the intermediary closes its copies, and no
  replayable filesystem state remains.
  A controlled delay proves the child remains live during capture, so hosted-runner
  diagnostic latency cannot invalidate the proof. The one-second lead therefore
  arms at 0.25 seconds after the authoritative lease has been established,
  instead of charging supervisor startup to the observer or relying on a
  zero-clamped threshold. The machine report exposes
  `forensicsSelfTestCaptureLeadValidated` and
  `forensicsSelfTestEvidenceHoldValidated`; the self-test fails unless the hold
  reports requested, granted, captured, released, completion delivered and
  target acknowledged. Neither an immutable process success nor failure outcome
  is captured while an already-started completion transaction is still
  publishing that acknowledgment. Owner failure performs terminate/reap first,
  then bounds this synchronization by the cleanup portion of the same transition
  budget; it is not a new observer deadline or cleanup authority.
- The adversarial owned-tree self-test gives the same `OwnedProcessLease` a
  bounded three-second operation window so hosted runtime startup can publish
  its fixture before quiescence is tested. This is an owner-assigned test
  budget, not an observer deadline or retry.
- Managed-stack collection can pause or otherwise perturb the observed child.
  `durationMs` remains the honest instrumented wall-clock value, while
  `diagnosticCaptureDurationMs` records collector wall time separately. These
  lifecycle timings are diagnostic evidence, not performance baselines.
- `Invoke-IsolatedProcess`, which holds the existing transition budget,
  allocates each observer capture a 15-second operation window and a five-second
  collector-cleanup allowance through
  `TransitionBudget.AllocateDiagnosticCollectorWindow`. The typed window uses
  the same monotonic `TimeProvider` and exposes the shorter of its allowance and
  the parent operation/cleanup remainder; PowerShell cannot start or renew a
  second timeline. `OwnedDiagnosticCollector` exclusively owns collector start,
  wait, terminate, authoritative reap and concurrent stdout/stderr drain, and
  returns typed evidence or a causal primary failure plus an immutable cleanup
  list. PowerShell receives no collector `Process` or cleanup target.
  `-ValidateForensics` drives the real `dotnet-stack` path through this boundary
  and requires capture-window, capture-lead, evidence-hold and remaining parent
  budget proof through `forensicsCollectorCaptureWindowSelfTestPassed`. Shared
  platform tests separately cover blocked collectors, cancellation, terminate,
  reap and drain failures, large dual-stream output, descendant retention and a
  mutation that consumes the parent budget. The lifecycle self-test records its
  typed `OperationDeadlineExceeded` failure, collector evidence and cleanup
  list in `forensicsCollectorCaptureWindowSelfTest`; a Boolean summary alone is
  not accepted as proof.
- PowerShell may receive collector failures through a PowerShell invocation
  wrapper. `Get-DiagnosticCollectorExecutionFailure` walks that exception chain
  only to recover `DiagnosticCollectorExecutionException`; lifecycle result
  rows retain the collector failure kind, typed evidence and typed cleanup
  stages for slow-phase and exit evidence. The helper does not classify target
  ownership or create a second failure aggregate.
- The supervisor copies target stdout and stderr to its owner-facing streams as
  bytes arrive. Normal completion still awaits both copy tasks, while failure
  cleanup retains output already published before the supervisor or target is
  terminated.
- The structured PowerShell gate scans both member invocation and command ASTs.
  Raw `Process`/`ProcessStartInfo` construction and `Start-Process`,
  `Stop-Process` or `Wait-Process` are forbidden in the transitive forensics
  closure. The whole-budget mutation must execute the real blocked-collector
  self-test; a source-only rejection is not behavioral proof.
- Execution slow-phase, post-teardown slow-exit and timeout evidence have
  separate arrays. A process-exit row cannot inherit unrelated execution
  evidence.
- Marker files are append-only fixture telemetry. The reader opens them with
  read/write/delete sharing and bounded retries because it samples while the
  fixture may be appending a state. Transient contention is counted; exhausted
  reads return to the bounded monitor loop, while a missing final marker still
  fails teardown and process-exit validation.
- On Windows, only native sharing and lock violations (`32` and `33`) count as
  contention. Other `IOException` values and `UnauthorizedAccessException` are
  retried as marker read errors and reported through `markerReadErrorCount` and
  `markerReadErrorType`; ACL or policy failures are never mislabeled as writer
  contention.

Schema 1 reports used the collector observation timestamp for `process-exit`,
so they include post-exit stdout/reporting overhead. In the
`20260729T102215715Z` report, the historical values remain teardown maximum
65 ms and process-exit maximum 170 ms. Schema 2 uses the OS exit timestamp and
must not be compared directly with that old exit metric.

## Static Ownership

`script/audit-lifecycle-ownership.ps1` scans production, test, benchmark and
tool sources for:

- module initializers and process-exit handlers;
- static constructors and static field initialization;
- external process creation;
- explicit threads, `Task.Run`, dispatchers and timers;
- global event registration;
- Generic Host startup and shutdown;
- synchronous waits, thread joins and synchronous file cleanup.

`docs/testing/assembly-lifecycle-owners.json` is the machine-readable ownership
policy. Each path must identify who starts the work, who stops it, and how
teardown performs cancellation, wake-up, wait and bounded join. An unowned or
unapproved mechanism fails the gate. The inventory is evidence, not a broad
suppression list.

## Profiles

| Profile | Iterations per assembly | Required use |
| --- | ---: | --- |
| `Local` | 1 | Script development and focused validation |
| `PR` | 3 | Every pull request on Windows |
| `Main` | 5 | Every push to `main` |
| `Rehearsal` | 100 | Release rehearsal and tag release gate |
| `Flaky` | 500 | Focused investigation; override up to 10000 |

Formal local Verification overrides the profile with `-Iterations 5` and runs
`-ValidateForensics`. Normal main validation runs five complete iterations;
tag release evidence uses the `Rehearsal` profile and deliberately runs 100.

Use `-AssemblyPattern` to isolate one or more suspect assemblies without
weakening the normal PR or release profiles:

```powershell
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Profile Flaky `
  -AssemblyPattern DownKyi.Desktop.Tests `
  -NoBuild `
  -ValidateForensics
```

## Timeout Forensics

On Windows, a slow phase or slow post-teardown exit automatically captures:

- managed process/thread IDs;
- `ThreadState`, wait reason and processor time;
- a sanitized process tree containing PID, parent PID and process name;
- `dotnet-stack report --process-id` output when the tool is available.

Confirmed non-quiescent owned trees use a separate evidence path and are written
to `residual-children.json`. The manifest records the lease failure, retained
root/target diagnostic IDs and OS containment identity. Job Object active-tree
state on Windows, delegated cgroup v2 membership on Linux and libproc anchored
group membership on macOS decide correctness. PID/PPID process-tree snapshots
remain observer evidence only. Reparenting or a changed PPID therefore cannot
hide a descendant, and evidence capture never converts `ResidualChildProcess`
to success.

CI installs the pinned Microsoft `dotnet-stack` tool and runs
`-ValidateForensics`. That self-test deliberately holds a marker-aware
`execution` probe beyond the slow threshold. It fails unless the same code path
used by test execution produces evidence and a non-empty managed stack.
On Windows it also opens a valid marker with exclusive sharing and proves that
the reader tolerates the temporary lock, then parses the marker after release.
Schema 4 records the process-lease and evidence-hold contracts. The
cross-platform process-supervision fixtures execute the platform
primitive, including launch-without-ownership mutations, parent-exit/reparent
behavior, inherited output handles and injected terminate/reap failures. Formal
`-ValidateForensics` also launches a parent that exits while its descendant
remains in the owned tree. The phase must be rejected as
`ResidualChildProcess`, bounded cleanup must finish, and the detailed
`processLeaseSelfTest` contract must pass. Timeout and slow-phase evidence are
observer operations within the caller's `TransitionBudget`; the self-test
injects observer failure and requires both that failure and the lease rejection
to remain visible. Observer code does not own a second kill, reap,
residual-child decision or deadline.

Schema 4 retains the marker-reader proof as a fail-closed object:

```json
{
  "markerReaderSelfTest": {
    "required": true,
    "executed": true,
    "passed": true,
    "contentionObserved": true,
    "contentionCount": 2,
    "recoveredAfterLockRelease": true,
    "markerParsedAfterRecovery": true,
    "errorType": null,
    "contractChecks": {
      "executed": true,
      "passed": true,
      "validProofAccepted": true,
      "errorTypeRejected": true,
      "zeroContentionRejected": true,
      "incompleteProofRejected": true,
      "errorClassificationPassed": true
    }
  }
}
```

The top-level `markerReaderSelfTestPassed` field is only a summary. Windows PR,
Main, Rehearsal and Flaky profiles require `-ValidateForensics`; missing,
skipped, unknown, non-contending or failed self-tests block the gate.
The self-test phase, top-level summary and final report use the same
`markerReaderSelfTestComplete` result,
including `contentionCount > 0` and `errorType == null`. Mutation checks prove
that a nominal `passed = true` cannot override an error, zero contention or an
incomplete proof.

Raw stdout/stderr, JSON evidence, the machine report and Markdown summary are
written below `artifacts/assembly-lifecycle/<run-id>/`. CI uploads the entire
directory even when the gate fails.

### Collector failure-report proof

With `-ValidateForensics`, schema 4 also records two compiled-collector
self-tests. The capture-window proof launches a blocked collector and requires
typed timeout, reap and stream-drain evidence without consuming the parent
operation budget. The cleanup-report proof sends a typed failure with a
non-empty immutable cleanup list through the same PowerShell converter used by
`Invoke-ForensicsObserverCapture`, JSON-round-trips the result and requires
these exact stage/cause pairs:

- `TerminateFailed` / `UnauthorizedAccessException`;
- `ReapDeadlineExceeded` / `TimeoutException`.

`forensicsCollectorCleanupReportSelfTestPassed` is a summary of the structured
`forensicsCollectorCleanupReportSelfTest` object, not a substitute for it.
Dropping or remapping either cleanup item makes the gate fail. The associated
review-invariant mutation executes the real lifecycle script and accepts only
the explicit cleanup-report self-test rejection; an unrelated child failure is
not proof.

## Comparing Results

Do not compare timing numbers collected on different machines. Every report
records:

- .NET runtime version;
- operating system and architecture;
- Commit SHA and whether the worktree was dirty;
- profile and iteration count;
- timeout and exit thresholds.

Only compare reports with compatible runner metadata, datasets and test
configuration. A faster rerun does not close a lifecycle failure; the owner and
teardown path must be identified and corrected.

## Completion And Rollback

A lifecycle fix is complete only when the relevant owner has a deterministic
teardown test, the focused flaky profile is stable, normal solution tests pass,
and PR plus release gates produce clean reports.

To roll back a gate change, revert the probe, scripts, policy, tests, docs and
workflow wiring together. Never retain workflow references to a removed
measurement phase, and never replace a failed lifecycle gate with a blind
rerun.
