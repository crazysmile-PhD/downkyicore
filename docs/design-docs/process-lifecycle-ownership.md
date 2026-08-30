# Process Lifecycle Ownership

Status: current design authority

This document owns the process-lifecycle boundary used by repository tests and
lifecycle diagnostics. It describes current behavior only; implementation
history belongs in Git.

## Invariant

`OwnedProcessLease` is the only component that owns a launched test or
diagnostic process. It owns start, ownership establishment, wait, caller
cancellation, timeout, bounded terminate, reap, owned-tree quiescence and
stdout/stderr drain. Callers may coordinate phases and render evidence, but
they must not start or clean up a parallel process path.

`TransitionBudget` provides one caller-owned monotonic operation deadline and a
reserved cleanup interval. A child collector may receive a smaller window; it
cannot extend or replace its parent's deadline.

## Owners

| Concern | Authority |
| --- | --- |
| Repository test selection, authorization and TRX result validation | `DownKyi.CentralTestRunner` |
| Target and descendant process lifecycle | `OwnedProcessLease` |
| OS containment and membership | `IProcessContainmentLease` implementations |
| Diagnostic collector process lifecycle | `OwnedDiagnosticCollector`, using `OwnedProcessLease` |
| Operation and cleanup deadline | `TransitionBudget` |
| Lifecycle phase orchestration and report rendering | `script/test-assembly-lifecycle.ps1` |

The PowerShell lifecycle script is a caller. It does not own termination,
reaping, membership convergence or stream closure.

## OS Ownership Primitives

- Windows uses a Job Object. The target cannot execute as an unowned process;
  the supervisor establishes job membership before authorizing execution.
- Linux uses a delegated cgroup v2 subtree. The target and descendants must be
  members of that subtree, and cleanup requires authoritative cgroup
  convergence.
- macOS uses an anchored process group plus authoritative membership queries.
  PID, PPID, process name and command line are diagnostic facts, not ownership
  authority.

The repository entry scripts use `delegated-cgroup-scope.ps1` when a Linux
runner must first enter an available delegated cgroup. That bootstrap does not
replace `OwnedProcessLease`.

## Lifecycle

1. The caller creates a `LaunchSpec` and one `TransitionBudget`.
2. `OwnedProcessLease.StartAsync` starts the supervisor and establishes the OS
   ownership primitive before the target is authorized to run.
3. The caller waits through the lease. Target exit alone is not completion;
   the owned tree must quiesce and redirected streams must drain.
4. On failure, timeout or caller cancellation, the lease consumes only its
   bounded cleanup reserve while it terminates, reaps and verifies quiescence.
5. The lease returns an `OwnedProcessOutcome` or throws an
   `OwnedProcessExecutionException` containing an `OwnedProcessFailure`.
6. The caller disposes the lease. Disposal failures remain separate cleanup
   evidence and do not replace the primary failure.

## Failure Contract

`OwnedProcessFailure` carries the typed failure kind, primary cause, target exit
code when observed, stdout, stderr, ownership identity, tree-quiescence result
and typed cleanup-stage failures. Cleanup stages remain ordered so terminate,
reap, membership convergence, stream drain and resource release failures can be
reported without obscuring the first causal failure.

Diagnostic collection is demand-driven. The lifecycle gate may collect stack
or process evidence for a failure, timeout, slow threshold or residual owned
process. A successful ordinary phase does not run a diagnostic self-test.

## Non-Owners

The following may be logged for diagnosis but cannot decide correctness:

- PID or PPID ancestry;
- process name or command-line matching;
- elapsed sleeps or retry counts;
- kill-all cleanup;
- a target exit code without owned-tree quiescence;
- captured evidence without completed process cleanup.

## Verification

Use the repository central entry points, never direct solution-wide
`dotnet test`:

```powershell
pwsh ./script/test-solution.ps1 -Configuration Release
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release -Profile PR -NoBuild
```

The platform-owned process-supervision tests cover real Windows, Linux and
macOS primitives. Architecture tests verify that lifecycle phases use one
`OwnedProcessLease`, one `TransitionBudget` and direct compiled central-runner
authorization.
