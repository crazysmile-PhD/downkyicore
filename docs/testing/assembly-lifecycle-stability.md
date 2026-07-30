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

## Dynamic Phases

`script/test-assembly-lifecycle.ps1` discovers every `*.Tests.csproj` and runs
each phase in an independent child process:

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

Every phase records its exit code, duration, timeout state, stdout/stderr
protocol state, residual child count, sanitized child identity and evidence
paths. Residual identity includes PID, parent PID, process name, creation time,
tree depth and a command line with repository, user-profile, temporary, URL and
credential values redacted. The report aggregates P50, P95, P99 and maximum
duration per assembly and phase.
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

## Profiles

| Profile | Iterations per assembly | Required use |
| --- | ---: | --- |
| `Local` | 1 | Script development and focused validation |
| `PR` | 3 | Every pull request on Windows |
| `Main` | 50 | Every push to `main` |
| `Rehearsal` | 100 | Release rehearsal and tag release gate |
| `Flaky` | 500 | Focused investigation; override up to 10000 |

Formal local Verification overrides the profile with `-Iterations 5` and runs
`-ValidateForensics`. Release evidence must run at least 50 iterations per
assembly; the repository's `Rehearsal` profile deliberately runs 100.

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

Residual children use a separate evidence path. The first observation is always
written to `residual-children.json`, even when a process exits before deeper
collection begins. A still-live managed child receives the normal thread,
process-tree and managed-stack capture; native descendants retain identity and
thread/process evidence without waiting on an inapplicable managed collector.
Any observed residual child remains a blocking `ResidualChildProcess`; evidence
capture never converts it to success and no grace period weakens the contract.

CI installs the pinned Microsoft `dotnet-stack` tool and runs
`-ValidateForensics`. That self-test deliberately holds a marker-aware
`execution` probe beyond the slow threshold. It fails unless the same code path
used by test execution produces evidence and a non-empty managed stack.
On Windows it also opens a valid marker with exclusive sharing and proves that
the reader tolerates the temporary lock, then parses the marker after release.
It additionally launches a deterministic residual `dotnet` child, requires the
gate to preserve its identity and evidence manifest, requires
`ResidualChildProcess` classification, and terminates the synthetic process tree
by matching both PID and creation time. The same self-test proves that private
paths, URLs, cookies and command-line secrets are redacted. Schema 2 exposes the detailed
`residualChildSelfTest` object and top-level `residualChildSelfTestPassed`
summary. Missing execution, identity, evidence, failure classification or
cleanup fails closed.
Timeout evidence is saved before the process tree is terminated.

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
