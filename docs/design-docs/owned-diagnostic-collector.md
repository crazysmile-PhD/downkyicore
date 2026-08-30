# Owned Diagnostic Collector

## Status And Authority

This design is implemented by implementation commit
`6fc71e406ba80b2ccfbff49e05023f76f72458b6` plus exact-head review-fix commit
`c3a3a33f67daa20ac450212433c69774385fb679`, follow-up commit
`e98c8a6c27cb22a44ffc63099af53a447139c6ee` and final-head capture-fixture
correction `d698f855afec7e2731b29d1643f473b201096207`. Native exact-head CI and a
clean same-head review of the later documentation checkpoint remain required
before this independent checkpoint is closed.

- Parent design: [Process Lifecycle Ownership](process-lifecycle-ownership.md)
- Parent migration: [PR #197 Process-Lease Migration](../exec-plans/pr-197-process-lease-migration.md)
- Follow-up plan: [PR #197 Owned Diagnostic Collector Migration](../exec-plans/pr-197-owned-diagnostic-collector-migration.md)
- Stage 3 closure HEAD: `531399c375700d2bd188fe8723878fad008b7058`
- Stage 3 implementation commit: `1298bf5cb4bcb69c2a5cb69ce07204ba782f51e8`

Stage 3 is closed. This checkpoint is an independent PowerShell-boundary
follow-up. It does not reopen or redesign Stage 3 target-process ownership.

## Implemented Boundary

The Stage 3 implementation moved evidence-hold state and target-process truth to
`OwnedProcessLease`. This follow-up moves the child-process lifecycle used to
run a diagnostic collector from
[`test-assembly-lifecycle.ps1`](../../script/test-assembly-lifecycle.ps1) to
`OwnedDiagnosticCollector`. The compiled boundary performs:

- collector process start;
- bounded wait and caller cancellation;
- collector terminate and reap;
- concurrent stdout/stderr drain;
- operation-timeout classification;
- preservation of a primary failure beside cleanup failures.

Those operations are process-lifecycle correctness even though the process is a
diagnostic tool. PowerShell now allocates capture policy, passes the typed
window and consumes typed evidence; it no longer depends on pipeline result
shape, raw collector-process state, exception-message inspection or manually
coordinated collector cleanup.

The ownership split is exact:

```text
supervised target process
  -> remains wholly owned by OwnedProcessLease

diagnostic collector's own child process
  -> moves to compiled OwnedDiagnosticCollector
```

The diagnostic collector may observe a target ID supplied for diagnostic use.
That ID does not make the collector an identity, containment, membership,
quiescence, terminate or reap authority for the target.

## Unique Owner Rules

### Target Process

`OwnedProcessLease` remains the target process's only:

- identity authority;
- containment authority;
- membership and quiescence authority;
- terminate and reap owner;
- target stdout/stderr drain owner;
- transition-budget owner.

No collector result may override the lease's target-process outcome, choose a
target-process cleanup action or convert a lease failure to success.

### Diagnostic Collector

`OwnedDiagnosticCollector` owns only the diagnostic collector child it starts.
For that child it owns start, wait, cancellation observation, bounded terminate,
reap, stdout/stderr drain, typed failure production and deterministic disposal.
It does not receive the target lease, target containment handle, target
membership query or a target kill/reap capability.

### Lifecycle Capture Policy

The lifecycle owner decides capture policy before invoking the collector. That
policy includes:

- which collector is needed, such as a managed stack or process snapshot;
- the immutable executable, arguments, working directory and environment;
- the operation capture allowance;
- the cleanup allowance;
- whether a diagnostic delay or another non-process capture is required;
- how a completed collector's exit code and output affect lifecycle evidence.

`OwnedDiagnosticCollector` executes that decision. It does not choose whether a
stack, snapshot or other collector should run.

### One Monotonic Timeline

The lifecycle owner allocates a collector window from the existing
`TransitionBudget`. The allocation is an attenuated view of the same monotonic
timeline:

```text
collector operation remaining
  = min(owner operation remaining, allocated operation remaining)

collector cleanup remaining
  = min(owner cleanup remaining, allocated hard-cleanup remaining)
```

The allocation must use the same `TimeProvider` and remain linked to the parent
budget. It can shorten the available time but can never extend, renew, replace
or outlive the parent budget.

The collector consumes a caller-allocated window. It must not create its own
15-second, five-second or other independent deadline. Moving the current
PowerShell stopwatch unchanged into C# would merely move a second deadline owner
between languages and is forbidden.

### Diagnostic Transition Timeline

`DiagnosticCollectorEvidence` carries diagnostic transitions on the same
monotonic collector window. Startup records request creation, collector
dispatch, process-start request, supervisor `Process.Start()` return,
containment preparation and establishment, independently completed control and
status pipe connections, ownership acknowledgment, launch-authorization write,
target-start acknowledgment and the public `ProcessStarted` boundary. A failed
start additionally records the exact
existing operation-deadline boundary, when the owner observed that boundary,
and begin/settled evidence for termination, tree quiescence, supervisor reap
and stream drain. Runtime observation then retains target attach, first
observable progress, stack capture, first stack byte, target exit, reap, drain
and typed return. Each item is `Observed`, `NotObserved` or `NotObservable`.

Observed entries are serialized in monotonic elapsed order. The exact deadline
entry is captured from one `TransitionBudget` observation;
it is not a second timer. Separating it from the owner's later observation
makes a synchronous Windows startup or cleanup call which crosses the assigned
boundary visible without using UTC or changing correctness.

The generic owner can directly observe its own process, streams, reap and typed
return. It cannot see `dotnet-stack`'s internal attach or stack-enumeration
boundaries, so those entries must remain explicitly `NotObservable` unless the
tool itself publishes a typed signal. Empty stdout/stderr before timeout is
evidence of no owner-visible progress, not permission to infer target identity,
runtime state or a correctness outcome. Timeline values never select a process,
extend a deadline or convert failure into success.

Every production outcome and typed failure also carries a bounded
`DiagnosticCollectorOwnerJournal`. It is an observational projection of the
same owner events, not a second observer or process owner. It retains only
observed transition records, typed failure and cleanup kinds, authoritative
supervisor/target identities where available, deadline/target/termination/
reap/drain state, and an automatically classified
`LastKnownGood -> FirstMissingRequired` interval. It contains no stack, stdout
or stderr payload. The normal evidence timeline can therefore be rejected or
lost while the low-volume journal still localizes the owner boundary.

Lifecycle artifact persistence is a separate transition. The PowerShell
observer copies the owner journal out of the typed collector result before it
writes the process-evidence artifact. A write failure reports
`EvidenceCaptured -> EvidencePersisted` as `EvidencePersistenceFailure`; it
does not rewrite that state as `SlowEvidenceMissing` alone. The final lifecycle
row preserves both its ordinary fail-closed label and the structural
localization. No UTC value, new deadline, retry or sleep participates in this
classification.

On Windows, `OwnedProcessLease` launches the already-built
`DownKyi.ProcessSupervision.exe` apphost as its inert supervisor. The apphost is
required and there is no hosted-`dotnet` fallback. Linux and macOS retain the
existing `dotnet <assembly>` launch. This removes the extra Windows muxer/runtime
startup layer before the supervisor pipe and ownership handshake; it does not
change the target launch protocol, sole-owner boundary, timeout values, retry
policy or cleanup semantics.

## C# Contract

The following API is a design sketch, not committed source. Names may be refined
during implementation, but the ownership and result shape are required.

```csharp
public sealed class TransitionBudget
{
    public DiagnosticCollectorWindow AllocateDiagnosticCollectorWindow(
        TimeSpan operationAllowance,
        TimeSpan cleanupAllowance);
}

public sealed class DiagnosticCollectorWindow
{
    public TimeSpan RemainingOperation { get; }
    public TimeSpan RemainingCleanup { get; }

    public Task DelayAsync(
        TimeSpan requestedDelay,
        CancellationToken cancellationToken = default);
}

public sealed record DiagnosticCollectorRequest(
    LaunchSpec Launch,
    DiagnosticCollectorWindow Window);

public sealed record DiagnosticCollectorEvidence(
    bool Started,
    bool Exited,
    bool Reaped,
    bool StreamsDrained,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record DiagnosticCollectorOutcome(
    DiagnosticCollectorEvidence Evidence);

public enum DiagnosticCollectorFailureKind
{
    StartFailed,
    OperationDeadlineExceeded,
    CallerCancelled,
    CollectorTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CleanupFailed,
    ExecutionFailed
}

public enum DiagnosticCollectorCleanupFailureKind
{
    TerminateFailed,
    CollectorTreeNotQuiescent,
    ReapDeadlineExceeded,
    ReapFailed,
    StreamDrainDeadlineExceeded,
    DisposeFailed
}

public sealed record DiagnosticCollectorCleanupFailure(
    DiagnosticCollectorCleanupFailureKind Kind,
    Exception Cause);

public sealed record DiagnosticCollectorFailure(
    DiagnosticCollectorFailureKind Kind,
    DiagnosticCollectorEvidence Evidence,
    Exception Cause);

public sealed class DiagnosticCollectorExecutionException : Exception
{
    public DiagnosticCollectorFailure Failure { get; }
    public IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures { get; }
}

public sealed class OwnedDiagnosticCollector
{
    public static Task<DiagnosticCollectorOutcome> CollectAsync(
        DiagnosticCollectorRequest request,
        CancellationToken cancellationToken = default);
}
```

`CollectAsync` is the complete ownership boundary. It returns only after the
collector has either completed and been reaped with streams drained, or failed
and exhausted bounded cleanup. The caller does not receive a disposable owner,
raw `Process`, process handle or kill/reap callback. Internal process and stream
objects are disposed by the compiled owner before the task completes.

All list-valued contract properties are initialized as immutable empty
collections when there are no items. An empty cleanup list is never represented
by `null` and never depends on PowerShell scalar/array pipeline behavior.

### Existing Types To Reuse

[`LaunchSpec`](../../tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs)
already snapshots executable, arguments, working directory, environment and
stdin policy. It is the immutable collector launch specification; creating a
second mutable launch DTO would duplicate authority.

`TransitionBudget` remains the root monotonic deadline owner. It should allocate
the new attenuated `DiagnosticCollectorWindow` so both views share one clock and
the child window cannot extend the parent.

`OwnedProcessLease` may be composed internally to own the collector process.
Its `OwnedProcessFailure` can be retained as an internal low-level cause and
mapped into collector-specific failure evidence. It must not be exposed as the
collector's public outcome: target ownership metadata, evidence-hold state and
collector capture semantics are different domains.

### New Types Required

The following types are intentionally separate from target-process contracts:

- `DiagnosticCollectorWindow`: a caller-allocated, non-renewable view of the
  existing budget;
- `DiagnosticCollectorRequest`: immutable collector launch plus its allocated
  window;
- `DiagnosticCollectorEvidence`: started/exited/reaped/drained/timeout evidence
  and captured streams;
- `DiagnosticCollectorOutcome`: a completed collector result, including a
  nonzero exit code when applicable;
- `DiagnosticCollectorFailure` and `DiagnosticCollectorFailureKind`: the causal
  collector failure;
- `DiagnosticCollectorCleanupFailure` and its enum: every genuine cleanup
  failure, in occurrence order;
- `DiagnosticCollectorExecutionException`: the typed C# aggregation boundary.

Enum values and typed properties are the contract. Exception message strings
remain human diagnostics and must not be parsed as the primary API.

## Failure Semantics

### Start Failure

Failure before a child starts produces `StartFailed` with `Started=false`.
Cleanup is attempted only for resources actually acquired. No synthetic
terminate or reap failure is added for a process that never started.

### Operation Timeout

Exhausting the caller-allocated operation window produces
`OperationDeadlineExceeded` and `TimedOut=true`. The owner then attempts
terminate, reap and stream drain within the cleanup portion of the same window.
The timeout remains the primary failure even if cleanup also fails.

### Caller Cancellation

Cancellation before start prevents launch. Cancellation after start becomes the
primary `CallerCancelled` failure and triggers bounded cleanup. Once cleanup has
begun, caller cancellation cannot abandon terminate, reap, stream drain or
internal disposal; the existing hard-cleanup allowance bounds that work.

The lifecycle caller may link its ordinary cancellation with the target lease's
read-only `TargetExitedToken` before invoking an observer. This is caller-owned
tool policy: it stops an attach or capture which can no longer describe a live
target. The collector still receives only a `CancellationToken`, never the
target lease, process truth, containment or cleanup authority.

### Terminate Failure

A terminate failure is recorded as `TerminateFailed` in `CleanupFailures`.
Reap and stream-drain attempts still run when they remain meaningful and
budgeted. Terminate failure cannot skip bounded reap.

If termination is the first failure during otherwise successful disposal, the
primary kind is `CleanupFailed`; the typed cleanup list retains the actual
terminate failure.

### Reap Timeout

If the collector cannot be authoritatively reaped within cleanup allowance, the
cleanup list records `ReapDeadlineExceeded`. The process ID is diagnostic only;
PID/PPID lookup cannot declare the collector reaped or select another process as
the cleanup target. A non-timeout root-reap failure is `ReapFailed`; a failed
owned-set quiescence check is `CollectorTreeNotQuiescent`. Neither can be
converted to success by a diagnostic process observation.

### Stream-Drain Timeout

Stdout and stderr begin draining concurrently at launch. If the collector exits
but either stream does not close before the allowed deadline, the primary kind
is `StreamDrainDeadlineExceeded`. If stream drain times out after another
primary failure, it is retained as a cleanup failure instead of replacing that
failure.

### Primary And Cleanup Failure Together

The first causal operation failure remains `Failure`. Every real terminate,
reap, drain or dispose failure is appended to the immutable cleanup list. A
later cleanup failure never overwrites the primary failure, and a catch/finally
path cannot discard either side.

### Collector Exited But Streams Remain Open

`Exited=true` is not completion. The result cannot be successful until
`Reaped=true` and `StreamsDrained=true`. A descendant that retains an inherited
stream must be handled through the collector's own compiled ownership boundary;
waiting indefinitely for EOF is forbidden.

### Clean Cleanup

Successful cleanup produces an immutable cleanup list with count zero. It is
never `null`, omitted or inferred from the absence of PowerShell pipeline
output.

### Nonzero Exit Code

A collector that starts, exits, is reaped and drains streams may return a normal
`DiagnosticCollectorOutcome` with a nonzero `ExitCode`. The lifecycle capture
policy decides whether that exit code means unavailable evidence, failed
evidence or another typed diagnostic status. The low-level process owner does
not reinterpret tool-specific exit codes.

### Owner Budget Already Exhausted

Window allocation fails before launch when the lifecycle owner's remaining
operation budget is zero. `CollectAsync` also fails closed without launch if the
supplied window is already exhausted. Neither path creates a replacement budget
or cleanup grace.

## PowerShell Final Role

> PowerShell 不得擁有 authoritative process lifecycle state machine。

This does not prohibit PowerShell from invoking an external executable. A thin
wrapper may remain:

```powershell
& dotnet DownKyi.Tool.dll @args
exit $LASTEXITCODE
```

PowerShell may own only:

- argv forwarding;
- environment forwarding;
- fixed executable invocation;
- immediate exit-code forwarding;
- GitHub output;
- non-authoritative logging and report formatting.

It must not receive the collector's raw `Process`, select a kill or reap target,
start or renew a deadline, aggregate exceptions, infer success from empty output
or parse exception messages as the contract.

## Linux Delegation Destination

Linux delegated cgroup acquisition is a shared execution-bootstrap concern. It
must not become private to a future `DownKyi.TestRunner`.

The compiled bootstrap must be reusable by both:

- a lifecycle runner invoking `OwnedDiagnosticCollector` or
  `OwnedProcessLease`;
- the central test runner invoking the same low-level process owner.

This checkpoint does not implement or modify delegation. It records the future
destination only. The existing delegated environment remains a precondition for
native Linux proof, and an unavailable or unproved delegation capability still
fails closed without PID/PPID fallback.

## Architecture Policy Gate

[`audit-lifecycle-ownership.ps1`](../../script/audit-lifecycle-ownership.ps1)
is a repository-wide architecture policy gate. It is not owned by the Stage 5
central-runner migration and must not be silently coupled to it.

Replacing that repository-wide gate belongs to an independent Architecture
Policy checkpoint or a deliberately scoped Stage 6 replacement. It remains
required. This collector checkpoint adds a narrower structured PowerShell-AST
guard over the transitive forensics closure; mutations that restore raw
collector process authority or an independent deadline make that guard fail.
The AST guard supplements the native collector behavior tests and does not
claim to prove runtime behavior by source inspection alone.

## Exact-Head Review Follow-Up

Codex review `5048306320` of checkpoint
`59339c3cb60118d5a6913c1c370b885b2bd306a4` found five in-scope gaps. Review-fix
commit `c3a3a33f67daa20ac450212433c69774385fb679` closes them without changing the
target-process ownership boundary:

- the whole-budget adversary now runs the real lifecycle script and is rejected
  by a blocked-collector behavioral self-test, not a source assertion;
- the PowerShell AST guard rejects command-based `New-Object`, `Start-Process`,
  `Stop-Process` and `Wait-Process` ownership as well as member expressions;
- cancellation observed after a collector start failure cannot replace the
  causal `StartFailed` classification;
- the PowerShell report unwraps the typed collector exception and records its
  failure kind, evidence and cleanup list;
- the supervisor relays target stdout and stderr while reading them, so failure
  cleanup cannot discard evidence produced before termination.

The deterministic collector self-test publishes its typed timeout evidence in
the lifecycle report. Shared platform tests also prove failure-output relay and
causal start-failure classification.

Codex review `5048688459` then inspected exact head
`0322d1c63f41d64a3d940d7792ba8f00d08a1259`. Its two in-scope findings are
closed by `e98c8a6c27cb22a44ffc63099af53a447139c6ee`:

- the structured `CommandAst` guard normalizes module-qualified commands to
  their leaf names before rejecting `New-Object`, `Start-Process`,
  `Stop-Process` and `Wait-Process`; its mutation uses the qualified forms;
- the observer and lifecycle self-test share one typed failure-to-report
  converter. A PowerShell fixture serializes a real typed failure with
  `TerminateFailed/UnauthorizedAccessException` and
  `ReapDeadlineExceeded/TimeoutException`, and a fifth adversarial mutation
  proves that discarding the non-empty cleanup list fails closed.

The previous exact head passed 20 native/shared CI checks with nine
release-only jobs skipped, but the review findings prevent treating it as the
closure head. Native CI and a clean review must converge again after this fix.

## Final-Head Capture-Window Fixture Correction

The capture-window self-test formerly allocated a one-second absolute child
window before supervisor launch, containment, named-pipe connection, ownership
acknowledgment and target start. It then launched `--block-forever`, which had
no target-side ready publication. A slower hosted start could therefore consume
the child window before `TargetStarted`; the collector correctly returned typed
`OperationDeadlineExceeded` with `Started=false`, while the self-test rejected
that state because it was intended to prove post-start timeout and cleanup.
This was a fixture/startup-publication race, not an
`OwnedDiagnosticCollector` deadline or cleanup defect.

The corrected fixture creates and verifies its non-completing blocking task
before atomically publishing a ready record. The lifecycle self-test uses a
three-second owner-assigned fixture window, requires that ready record and the
pre-block stdout/stderr markers, and still requires the causal timeout,
authoritative reap, complete stream drain, an empty cleanup list, bounded
elapsed time and preserved parent budget. The three seconds are local test
policy for hosted startup; no production or global deadline changed.

Before any phase-level throw, the structured self-test result is written to
`forensics-collector-capture-window-self-test.json` and emitted in compact form
to the job log. A one-millisecond startup-window mutation deterministically
restores pre-ready deadline exhaustion. A second mutation publishes ready
before creating the blocking task. The same behavioral lifecycle gate rejects
both mutations, so source shape or an arbitrary exception cannot satisfy the
proof.

## Formal-Phase Attach/Shutdown Diagnosis

Strict PR run `33165929460`, Assembly Lifecycle job `98831200568`, produced the
only failure at `DownKyi.Core.Tests` iteration 2 execution. The real target ran
for approximately 4.038 seconds through OS exit, but the synchronous observer
added a 15.019-second empty `dotnet-stack` capture and the old stopwatch reported
19.210 seconds. The collector had started, timed out, exited, reaped and drained
with no cleanup failure, so the ownership contract remained intact.

The pinned `dotnet-stack` version is `9.0.661903`, invoked as
`dotnet-stack report --process-id 8852`. Its report path opens the EventPipe
session before creating trace output or printing progress. The failed artifact's
empty streams and teardown ordering therefore narrow the stall to diagnostics
attach, before stack enumeration or publication. A deterministic Windows
fixture confirms the class: the exact tool connects to `dotnet-diagnostic-PID`,
the fixture accepts and withholds the reply, and the typed window expires with
no output while reap/drain stay bounded. A separate exact-tool experiment which
exits the target after a session begins returns promptly with
`ServerNotAvailableException`, excluding post-session shutdown as the 15-second
consumer.

Implementation checkpoint `35606b5cdbd7a011b7a515fd7a6aa28c8c4f9039`
changes lifecycle observer policy, target-exit timing evidence and diagnostic
instrumentation only. It does not change the 15-second allowance, the parent
budget, collector ownership, retry behavior or cleanup semantics.

## Parent-Budget Proof Classification

Strict PR run
[33225859743](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33225859743),
Assembly Lifecycle job
[99029376828](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33225859743/job/99029376828),
failed at source head `2953afc4c259ae0a81a7f787d74a7e53fad7966e`
only because `parentBudgetPreserved` required more than 1,000 ms and observed
962.370 ms. The collector still returned typed `OperationDeadlineExceeded`,
started, exited, reaped and drained both streams with no cleanup failure; all
other capture-window predicates and lifecycle ownership 644/0 passed.

The 1,000 ms value was a self-test proof threshold, not a correctness contract
or safety margin. The implemented design requires an attenuated window on the
same monotonic parent timeline and a positive, unexhausted parent operation
budget after collector completion. The original review-fix self-test in
`c3a3a33f67daa20ac450212433c69774385fb679` used a five-second parent and a
one-second child while checking for more than one second remaining. Fixture fix
`d698f855afec7e2731b29d1643f473b201096207` changed the hosted child allowance
to three seconds but retained the old numerical threshold and an outer
four-second stopwatch predicate. Neither number defined collector production
semantics.

The failed artifact separates the consumer. `ProcessStartRequested` occurred
after 5.189 ms, `ProcessStarted` after 320.268 ms and first stream progress after
370.365 ms. Reap and drain completed after 2,978.652 and 2,976.078 ms, and the
typed result was produced after 2,984.523 ms. The outer self-test stopwatch
stopped after 3,997.680 ms, and the root budget plus surrounding ready-file work
had consumed 4,037.630 ms. The old artifact did not timestamp ready publication
separately, so target-side IPC and hosted scheduling inside the first 370.365 ms
cannot be subdivided; both nevertheless completed before the typed child
deadline. The extra post-result interval is therefore caller-side PowerShell
exception delivery/mapping, surrounding self-test work and hosted scheduling,
not collector operation, cleanup, supervisor startup, process start or IPC. New
diagnostic-only `callerTiming` fields split task settlement from failure mapping
on later runs; they do not select pass/fail.

Implementation `9c8f9765ca207116324a776c27ed973184710756`
replaces hosted timing margins with direct contract predicates: the typed child
operation window must be exhausted and the parent operation budget must remain
strictly positive. The report retains raw milliseconds plus
`deadlineAuthority`, including the parent value before allocation, allocated
allowance, attenuation result and both remaining budgets. The whole-parent
mutation records a non-attenuated allocation, zero child and parent remainder,
and makes only the parent-budget contract predicate false while timeout,
reap/drain and empty cleanup stay true. No timeout, retry, sleep, fresh deadline,
collector owner or Stage 4 path changed.

## Stage 3 Assembly Lifecycle Slow-Evidence Self-Test Blocker Checkpoint

Starting exact head `253e1023e6e62191d331867b6f607ee049f3a4de`
naturally triggered Strict PR run
[33247269844](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33247269844).
Its only required failure was the Windows Assembly Lifecycle job. Ownership
reported 658 matches and zero violations, no formal lifecycle phase started and
all four slow-evidence scenario results were null. The outer
`AggregateException` and final one-second-lead message did not identify a timing
failure.

The failed artifact nevertheless contained the configured scenario's completed
slow-phase process evidence. A local exact-head reproduction with causal
exception expansion identified the first failed transition as
`configured.cleanup.authorization-dispatch`: the target and collector path had
completed, but `Invoke-IsolatedProcess` unconditionally invoked
`Close-DownKyiTestProcessAuthorization` with a null authorization. PowerShell
then resolved the helper's unloaded
`DownKyi.CentralTestRunner.CentralTestAuthorization` parameter type and threw
`System.Management.Automation.RuntimeException` with message `Unable to find
type [DownKyi.CentralTestRunner.CentralTestAuthorization].` The cleanup-only
failure was wrapped as `Lifecycle owned child-process cleanup failed.` This was
not a readiness race, collector/target-exit race or collector timeout.

Implementation `4e38fed05466df44c3b6a5a34d3e9620994a712b` skips that typed
cleanup helper when no authorization was created. It does not modify the helper
or any Stage 5 central-runner code. The
ordering self-test now runs configured, one-second, immediate-dispatch and
slow-completion scenarios independently and records target start, atomic ready
observation, collector arm, authoritative target exit, collector completion,
caller cleanup completion and fault boundary for each scenario. Aggregate
evidence retains the outer exception and every inner type/message/stack, the
first causal exception, an optional primary failure and separate cleanup
failures. The executable separation fixture covers primary-plus-cleanup and
cleanup-only shapes.

Ready-file PIDs and lease target PIDs remain diagnostic-only output. Readiness
correctness uses the invocation-owned atomic ready file and its scheduled-delay
contract, while target exit remains the typed `OwnedProcessLease` transition.
No timeout, timing threshold, sleep, retry, capture window, mutation, collector
owner, target owner or `TransitionBudget` changed. Local focused evidence after
the correction recorded all ordering predicates true, one assembly, nine
formal phases, zero failures and ownership 658/0; the focused Architecture class
passed 11/11 through the prebuilt authorized central-runner path.

## Non-Goals

This checkpoint excludes:

- Stage 4 restart transaction work;
- Stage 5 central test runner work;
- TRX migration;
- test authorization migration;
- Linux delegation implementation;
- workflow rewriting;
- release, security or API tooling;
- replacement of `audit-lifecycle-ownership.ps1`;
- Stage 3 target-process redesign;
- a one-shot repository-wide PowerShell rewrite.
