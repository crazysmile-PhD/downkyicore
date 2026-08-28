# Owned Diagnostic Collector

## Status And Authority

This design is implemented locally by implementation commit
`6fc71e406ba80b2ccfbff49e05023f76f72458b6`. Native exact-head CI and
same-head review remain required before this independent checkpoint is closed.

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
