# Restart Handoff Lifecycle

## Status

Stage 4A feasibility remains complete and authoritative for the domain choice.
Production Stage 4 now implements that separate bounded restart domain through
`RestartHandoffLease`, `RestartHandoffHelper` and native
`ParentLifetimeLease` backends. The product caller is migrated; exact-head
cross-platform CI and same-head review remain the closure authority for this
implementation checkpoint. The production implementation and executable proof
commit is `12fbde8647d0a8ddc907264f3ab10741f84e966a`.

## Confirmed Conflict

The original Stage 4 plan treated a committed restart helper as an ordinary
`OwnedProcessLease` child. Exact-head evidence proves that composition is
invalid:

1. `OwnedProcessLeasePlatformTests.OwnerLifetimeClosureTriggersBoundedOwnedTreeCleanup`
   closes the owner-lifetime channel while the owned target is still running and
   requires bounded terminate, quiescence and reap. The companion
   `OwnerDeathAfterTargetExitStillTerminatesRetainedDescendants` proves the same
   invariant after the root exits while descendants remain.
2. `ProcessRestartLauncherTests` and `AvaloniaApplicationLifecycleTests` preserve
   the restart product transaction: prepare a helper, commit only after desktop
   termination handoff is accepted, let the helper survive the exact old desktop
   exit, launch one replacement, then terminate the helper.
3. `OwnedProcessLease` exposes launch, wait and disposal. Its supervisor accepts
   finalization only after target exit and authoritative tree closure. It has no
   transfer, detach or committed-successor transition that can outlive owner EOF
   without disabling the Stage 2 owner-death cleanup invariant.

Therefore:

> Stage 4 original composition assumption invalidated.

This is not a Stage 2 defect. An ordinary owned child and a committed restart
successor belong to different lifetime domains.

## Domain Boundary

```text
ordinary owned child
  owner lifetime EOF
    -> bounded terminate
    -> authoritative quiescence
    -> reap and stream drain

restart handoff domain
  Prepared
    -> ExactParentWatcherArmed
    -> Ready
    -> Authorized
    -> Committed
    -> ExactParentExited
    -> RelaunchAttemptedExactlyOnce
    -> TerminalExit

  any pre-commit failure
    -> RevokedOrRejected
    -> NoRelaunch
    -> TerminalExit
```

The post-commit helper is a bounded transaction successor. It is not a detached
daemon or persistent service. Its only authority is immutable one-shot restart
authorization, its exact-parent watcher and the remaining portion of the single
handoff deadline. It cannot acquire a new timeout, accept a second commit or
remain alive after a terminal outcome.

## Feasible Platform Contracts

Stage 4A proved these contracts natively in its feasibility fixture. Production
Stage 4 implements the same contracts in `tools/DownKyi.ProcessSupervision` and
routes the native platform test projects through a production restart fixture.

### Windows

Acquire a process `HANDLE` while the old desktop is alive, retain only the
rights required to wait, and report `Ready` only after the exact process object
can be waited. The handle, not the numeric PID or start time, is correctness
authority. Process exit signals that retained object even if the numeric PID is
later reused.

### Linux

Call `pidfd_open` while the old desktop is alive and wait for readiness with
`poll` or `epoll`. The pidfd, not PID, PPID, `/proc` enumeration or start time,
is correctness authority. Unsupported pidfd behavior is a capability failure;
there is no polling fallback.

### macOS

Register `EVFILT_PROC` plus `NOTE_EXIT` in a `kqueue` while the old desktop is
alive. Return `Ready` only after the registration succeeds, and do not permit
the desktop to finish handoff before that readiness. PID observation is only the
input used to arm the kernel watcher; it is not a later polling authority.

## Deadline Handoff Feasibility

Prepare fixes one immutable absolute deadline in a platform monotonic-clock
domain. The helper receives the clock-domain identifier, absolute expiry and any
required conversion metadata as authenticated handoff data. It may read that
same clock only to calculate remaining time. It cannot restart a stopwatch,
renew the product window or create an independent `WaitAsync` window.

The production transaction serializes the prepared clock domain, operation
expiry, cleanup expiry and frequency in the authenticated handoff. Time consumed
by prepare, watcher arming and authorization therefore reduces the helper's
parent-wait and relaunch window. The helper has no deadline constructor or fresh
stopwatch authority.

## Contract Responsibilities

`ParentLifetimeLease` answers only whether the exact original parent exited. It
does not own or terminate the helper, authorize restart, commit a transaction or
create a deadline. `RestartHandoffLease` owns the prepared helper, the two named
pipe endpoints, one transition budget and one one-shot commit/revoke decision.
After commit, `RestartHandoffHelper` owns only the retained exact-parent watcher,
the authenticated deadline and one relaunch attempt before terminal exit.

`OwnedProcessLease` remains unchanged and continues to own ordinary launched
children. No `IgnoreOwnerDeathAfterCommit` flag or restart-specific weakening is
permitted.

## Feasibility Result

Exact source head `689c5d6c41b3a3a7b8a0c6a318c80a4ebe737879`
passed Strict PR run
[33161523853](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853).
Windows, Linux and macOS each passed all 16 restart-handoff cases and all four
shared IPC-naming cases. The platform jobs exercised a retained Windows process
handle, Linux pidfd plus poll and an armed macOS kqueue `EVFILT_PROC` watcher.
The corpus proved watcher readiness, authorization EOF/partial/replay rejection,
pre-commit and committed parent exit, parent hang, helper crash, deadline
exhaustion, relaunch failure, exactly-once relaunch, terminal helper closure and
all four required mutations.

The identity-reuse proof deterministically substitutes a rebound logical PID
slot/authority; it does not wait for the host OS to recycle a numeric PID. The
retained native object remains authority for the original process, and the
numeric-authority mutation is rejected. That proves the required authority
boundary without claiming a probabilistic observation of real numeric PID
recycling in CI.

The ordinary owner-death regression remains intact. Its failing macOS path also
established a separate IPC architecture defect: descriptive logical labels had
been embedded in physical pipe identifiers. `IpcEndpointName` now retains those
labels only for diagnostics and supplies every repository pipe server with a
21-character ASCII identifier under a 24-character policy ceiling.

Source-shape checks are supplemental only. Missing native capability, a second
deadline owner, PID/PPID correctness, weakened Stage 2 cleanup, an unbounded
helper or a persistent service would trigger the Stage 4 stop rule. None is
present in the production implementation.

## Production Stage 4 Checkpoint

`ProcessRestartLauncher` now prepares `RestartHandoffLease`; it no longer parses
or waits on PID plus start time, owns a raw detached helper, uses an anonymous
commit byte or falls back to the old path. `DesktopApplication` enters helper
mode through the typed protocol before Avalonia starts. Physical authorization
and readiness endpoints come only from `IpcEndpointName`.

The transaction state is typed and one-way:

```text
Prepared -> WatcherReady -> Authorized
  -> Revoked -> terminal helper cleanup/reap
  -> Committed -> ParentExited -> RelaunchStarted -> Completed
  -> Failed
```

Authorization is a fixed, nonce-bound, deadline-bound frame followed by channel
EOF. Empty, partial, malformed, replayed and multi-transition payloads fail
closed. Commit and revoke are mutually exclusive. Revocation closes the channel,
terminates the prepared helper, waits within the original cleanup deadline and
preserves concurrent cleanup failures. Committed relaunch is one-shot: a start
failure is terminal and is never retried.

Avalonia Policy B is unchanged. Cleanup failure plus accepted desktop handoff
still commits and preserves cleanup evidence. Desktop handoff or handoff-protocol
failure revokes the prepared helper. Cleanup, desktop and helper failures remain
separate causal entries in the existing lifecycle aggregation.

The blocking-review cleanup remediation keeps that transaction boundary but
adds typed helper-terminal cleanup evidence. Status endpoint, authorization
endpoint and exact-parent lease disposal are attempted independently in Policy
B order. A relaunch, authorization or parent-wait transition remains the first
causal outcome even when one or more later cleanup stages fail; cleanup-only
failure leaves the completed transition visible but makes the aggregate outcome
unsuccessful. The fault injection used to prove this is internal to the native
production fixture and is not a helper command-line or environment option.
Local Windows proof passed all 19 production restart cases, 14 affected
architecture/mutation cases and the nine-test cleanup mutation profile with
exactly one owning failure. Linux and macOS native regression remains part of
the later unified exact-head closure rather than evidence inferred from this
Windows run.

Local Windows proof at the implementation checkpoint passed 32 production and
Stage 4A restart cases, 30 affected architecture cases, 23 desktop restart and
Policy B cases, and all 328 Architecture tests. The full review-invariant gate
passed 333 normal tests and rejected all 15 adversarial profiles. The eight
Stage 4 mutations each executed eight tests and failed exactly its owning test:
numeric identity, early READY, fresh deadline, ordinary lease composition,
authorization replay, relaunch retry, reversed parent ordering and missing
reap. Linux pidfd and macOS x64/arm64 kqueue production execution remain pending
exact-head CI; local cross-platform project compilation is not native proof.

## Native Contract References

- Windows retained process objects and wait handles:
  [OpenProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocess)
  and [Terminating a Process](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process).
- Linux exact process descriptors:
  [pidfd_open(2)](https://man7.org/linux/man-pages/man2/pidfd_open.2.html) and
  [poll(2)](https://man7.org/linux/man-pages/man2/poll.2.html).
- macOS exact exit notification:
  [kqueue(2)](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/kqueue.2.html).
- Cross-process monotonic deadline basis: .NET
  [Unix Stopwatch](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Stopwatch.Unix.cs)
  and [Windows Stopwatch](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Stopwatch.Windows.cs).
