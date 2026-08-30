# PR #197 Stage 4A Restart Handoff Feasibility

## Goal

Determine whether a restart helper can legally survive the old desktop process,
prove that exact exit, relaunch once and terminate without becoming unowned or
receiving a renewed deadline. This plan stops before production Stage 4.

Owner branch: `fix/pr196-review-followup`

Starting exact HEAD: `89af7b16f774c98e8b717cd0fbd7d5c70328e761`

Pull request: #197

## Confirmed Starting Decision

`OwnedProcessLease` owner-lifetime EOF behavior and the current restart product
contract cannot be directly composed. The lease has no existing proven transfer
transition. The original Stage 4 composition assumption is invalidated; Stage 2
ordinary child ownership remains authoritative and unchanged.

The candidate architecture is a separate bounded restart domain. It may compose
`ParentLifetimeLease`, typed one-shot authorization and a cross-process immutable
deadline. It must not duplicate generic tree containment or introduce a restart
exception flag into `OwnedProcessLease`.

## Scope

This checkpoint may add repository-hosted, test-only native fixtures and typed
evidence. It may update the process ownership design and this execution plan.
It does not migrate `ProcessRestartLauncher`, implement production
`RestartCoordinator` or `RestartHandoffLease`, change product behavior, redesign
Stage 3, start Stage 5 or Stage 6, edit workflows or alter release artifacts.

## Native Feasibility Matrix

| Platform | Candidate exact-parent authority | Required readiness boundary | Forbidden fallback |
| --- | --- | --- | --- |
| Windows | retained process `HANDLE` | handle acquired while parent is alive, then `Ready` | PID plus start time |
| Linux | `pidfd_open` plus `poll` | pidfd opened while parent is alive, then `Ready` | PID/PPID or `/proc` polling |
| macOS | `kqueue` `EVFILT_PROC` `NOTE_EXIT` | watcher registration succeeds, then `Ready` | PID/PPID/start-time polling |

Every platform reports typed state and terminal evidence. A capability error is
fail-closed evidence, not a skipped success.

## Required Behavioral Proof

The test-only harness must prove:

1. the exact parent watcher is established before handoff;
2. stale identity is rejected;
3. numeric PID reuse cannot become exact-parent proof;
4. authorization EOF causes no relaunch;
5. partial authorization causes no relaunch;
6. replayed authorization causes no second transition;
7. parent exit before valid commit causes no relaunch;
8. committed normal parent exit permits one relaunch;
9. a parent hang after commit consumes the existing deadline and fails closed;
10. helper crash produces a terminal failure with no relaunch;
11. deadline exhaustion before parent exit fails closed;
12. relaunch-start failure is terminal;
13. a successful relaunch is attempted exactly once;
14. every terminal path leaves no helper;
15. a fresh helper-clock mutation is rejected;
16. a watcher-armed-after-parent-exit mutation is rejected;
17. a numeric-PID-authority mutation is rejected;
18. an ordinary owner-death lease used across commit is rejected.

Tests must assert typed state/evidence. Source searches may guard architecture
shape but cannot substitute for native executable results. Synchronization uses
explicit readiness and authorization events; sleeps, retries and PID polling may
not make the proof green.

## Deadline Feasibility

Prepare creates one absolute deadline in a platform monotonic-clock domain.
Authenticated handoff material carries that immutable expiry and required clock
metadata. The helper reads only remaining time in the same domain. The fixture
must show that parent-side consumption reduces helper-side remaining time and
that a renewed full window is observably rejected.

The executable probe decides the concrete representation. It must not create an
independent helper stopwatch or `WaitAsync` window.

## Validation Order

1. Run the existing owner-lifetime and restart product-contract tests.
2. Run the feasibility fixture and mutation corpus on Windows.
3. Run architecture and documentation guards.
4. Push the test-only checkpoint and require native Linux and macOS execution on
   the exact PR head.
5. Record the native outcome in this plan and the restart design.
6. Re-run exact-head gates after the result-only documentation checkpoint.

## Outcome

Stage 4A is behaviorally feasible at implementation head
`689c5d6c41b3a3a7b8a0c6a318c80a4ebe737879`. Strict PR run
[33161523853](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853)
completed successfully with that source head. GitHub tested merge commit
`d75ffc90578130916dbff6884b720c0115c26a4a`; its second parent is the exact
source head above.

Native evidence:

- [Windows](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853/job/98816882339):
  16/16 restart-handoff cases and 4/4 IPC-naming cases passed using a retained
  process handle as exact-parent authority.
- [Linux](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853/job/98816882228):
  16/16 plus 4/4 passed using `pidfd_open` and `poll`.
- [macOS](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853/job/98816882251):
  16/16 plus 4/4 passed using an armed kqueue `EVFILT_PROC` `NOTE_EXIT`
  watcher.
- [Assembly Lifecycle](https://github.com/crazysmile-PhD/downkyicore/actions/runs/33161523853/job/98816882086):
  627 ownership matches, zero violations, zero failed phases and zero residual
  children.

The tests cover all 18 required proof statements; parameterized cases produce
16 executions per platform. The logical PID-reuse case deterministically
substitutes a rebound PID slot/authority rather than waiting for actual numeric
PID recycling. The retained kernel object remains bound to the original parent,
and the numeric-PID-authority mutation fails.

Local pre-push evidence was 47/47 affected Windows process-supervision tests,
30/30 affected Architecture tests, 9/9 Assembly Lifecycle architecture tests,
627 ownership matches with zero violations, warnings-as-errors builds and a
clean formatter result.

The macOS ordinary-lease regression exposed a repository-wide IPC naming
defect, so the checkpoint also introduced `IpcEndpointName`: logical diagnostic
labels are separate from 21-character physical `dkyi-` identifiers, with a
24-character ceiling and 80 bits of cryptographic randomness. It migrated the
ordinary lease control/status endpoints and both Stage 4A temporary endpoints.
Regression proof covers fixed ASCII length, 16,384 parallel unique identifiers
under case-insensitive comparison, logical-label length independence and the
104-byte macOS Unix-domain socket path budget. An architecture test rejects new
pipe-server call sites that bypass the policy.

## Blocking-Review Cancellation Remediation

The Stage 4A identity and feasibility decision remains closed. The later
blocking review exposed one production wait-boundary defect: once
`WaitForSingleObject`, pidfd `poll`, or `kevent` had begun, caller cancellation
could not wake the call before the original deadline. The remediation keeps the
same retained exact-parent authority and immutable deadline while adding a
non-authoritative cancellation signal to each native wait set:

- Windows: token wait handle beside the retained `SYNCHRONIZE` process handle;
- Linux: private eventfd beside the retained pidfd;
- macOS: `EVFILT_USER` beside the retained `EVFILT_PROC/NOTE_EXIT` registration.

There is no PID/PPID/start-time fallback, sleep, retry, renewed deadline or
second owner. A cross-process fixture cancels only after an internal observation
proves the native parent wait has started. A separate race proof establishes the
parent event before cancellation and requires exact-parent exit to win. Local
Windows validation passed all 21 production restart cases and all 16 affected
architecture/mutation cases. The ten-test cancellation mutation profile failed
exactly its owning test. Native Linux and macOS behavior is not inferred from
the Windows result and remains a required exact-head CI closure condition.

## Completion Conditions

All three native backends, the deadline handoff and all four mutations passed on
one exact source head. Stage 4A is complete. Production Stage 4 remains
separately deferred after this result.

Any required primitive that is unavailable or ambiguous, any need for PID/PPID
correctness, a renewed deadline, weakened Stage 2 owner death, an unbounded
helper, a persistent service or Stage 5 work stops this checkpoint.

## Commit And Rollback

The checkpoint keeps four independent review boundaries:

1. `433e76f` `docs: record restart handoff design conflict`;
2. `544ad3d` `test: prove restart handoff feasibility`;
3. `689c5d6` `test: harden restart IPC naming and watcher mutation`;
4. `docs: record restart handoff feasibility result`.

Rollback removes only the Stage 4A fixture and these documentation deltas.
Existing Stage 2 and Stage 3 production behavior remains unchanged.
