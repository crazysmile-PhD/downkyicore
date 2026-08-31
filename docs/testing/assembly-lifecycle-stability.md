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

### Single process owner proof contract

`OwnedProcessLease` is the only public lifecycle authority for an isolated
phase. The PowerShell gate supplies an immutable `LaunchSpec`, one caller-owned
monotonic `TransitionBudget`, and the required containment strength, then uses
only `StartAsync`, `WaitAsync`, and `DisposeAsync`. The supervisor host,
platform capability, protocol, and containment backends are internal seams;
diagnostics, report rendering, PowerShell, and workflow code cannot directly
start a process, terminate, reap, or drain the phase as a second owner.

The 2.1 architecture contract is:

| Question | Authoritative answer |
| --- | --- |
| WHO | `OwnedProcessLease` owns start, containment, wait, cancellation/deadline response, termination, reap, quiescence, stream drain, bounded cleanup, and lifetime close. |
| WHAT | One supervisor/target containment and its immutable typed proof outcome. |
| ENTRY | `StartAsync(LaunchSpec, TransitionBudget, ProcessContainmentRequirement)`, followed by one `WaitAsync`; `DisposeAsync` closes the same ownership lifetime. |
| EXIT | The owner publishes once, only after operation handling and bounded cleanup/resource close have completed. |
| STATE | Every required invariant is exactly `Proven`, `Violated`, or `Unknown`. |
| FAILURE | All typed operation and cleanup failures are retained; later cleanup evidence cannot replace earlier facts. There is no `PrimaryFailure` contract. |
| CAPABILITY | The caller declares required strength; an internal immutable-fact router selects the backend and the outcome reports the strength actually established. |
| CLOCK | The caller creates one monotonic `TransitionBudget`; only the lease consumes its operation and cleanup windows. |
| RACE | Invariant truth is absorbing for violations and independent of callback/thread scheduling; proof snapshots are deterministic sets, not an event race. |
| FACT | Direct target wait, containment membership, budget, reap, stream, and resource-close observations are authoritative. Diagnostic timestamps, stacks, logs, and raw evidence cannot change an invariant. |

The formal decision is deliberately small: all required invariants must be
`Proven`; `Violated` fails, and `Unknown` fails closed. Multiple failure facts
may coexist. No timeout, cancellation, target-exit, containment, or cleanup fact
competes for unique first-causal status, and 2.1 defines no cross-source global
timeline. CI and report code consume `FormalGatePassed` plus the typed invariant
and failure collections; they may render raw facts as diagnostics but must not
re-derive lifecycle correctness from them.

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
   deadline with typed tree-quiescence proof and no runner-protocol pollution.

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
protocol state and child-process observations. A child observed immediately
after the parent exits is `transient` when it drains inside the bounded
500-millisecond quiescence window and `residual` only when the same process
identity remains alive at the boundary. Identity includes PID, parent PID,
process name, creation time, tree depth and a command line with repository,
user-profile, temporary, URL and credential values redacted. The report
aggregates P50, P95, P99 and maximum duration per assembly and phase.
Every phase result also has general `failureType` and `errorType` fields.
`slowEvidenceErrorType` is reserved for failures inside slow-evidence capture
and must not carry unrelated self-test or lifecycle contract failures.

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
- `-ValidateForensics` uses its held child to prove the capture lead actually
  ran before a synthetic 1.25-second threshold. The one-second lead therefore
  arms at 0.25 seconds instead of relying on a zero-clamped threshold. The
  machine report exposes `forensicsSelfTestCaptureLeadValidated`; the forensics
  self-test fails when that value is false, even if a later stack capture would
  otherwise exist.
- Managed-stack collection can pause or otherwise perturb the observed child.
  `durationMs` remains the honest instrumented wall-clock value, while
  `diagnosticCaptureDurationMs` records collector wall time separately. These
  lifecycle timings are diagnostic evidence, not performance baselines.
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

The process-supervision entries are intentionally path-specific. The lease is
the lifecycle owner; the supervisor capability is subordinate to its protocol,
the router and protocol files are pure seams, and the Linux cgroup backend is
the selected platform authority for its exact cgroup. These entries must not be
collapsed into a `tools/**` allowance, and neither lifecycle PowerShell nor
diagnostic collectors are registered as process owners.

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

## Diagnostic Forensics

A slow phase or slow post-teardown exit writes a diagnostic-only target
reference and, when available, runs `dotnet-stack report --process-id` through
a separate `OwnedProcessLease`. The collector has its own 15-second operation
budget and bounded cleanup. PowerShell never starts, waits for, kills or reaps
the collector directly. Its stdout/stderr, typed invariant result and failures
are retained beside `managed-stack.txt` in
`managed-stack.owned-process.json`.

Collector availability, timeout or failure is recorded in the evidence
manifest as diagnostic status and error data. It never changes the target
lease's `FormalGatePassed`, invariants or failures. Likewise, the lifecycle
gate does not infer tree state from PID polling, process names or raw diagnostic
output. Only the target lease's typed `TreeQuiescence` invariant is
authoritative; `Unknown` and `Violated` fail closed inside that owner.

CI installs the pinned Microsoft `dotnet-stack` tool and runs
`-ValidateForensics`. The held probe exercises the same diagnostic path and
reports whether a managed stack was captured before the classification
threshold, but that observation remains separate from target lifecycle truth.
On Windows the validation also opens a valid marker with exclusive sharing and
proves that the reader tolerates the temporary lock, then parses the marker
after release. The former residual-child PID polling, synthetic persistent
child, and manual PID-plus-creation-time cleanup contract is superseded by the
lease's authoritative containment and quiescence proof.

Schema 2 records the marker-reader proof as a fail-closed object:

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
