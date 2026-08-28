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

## Completion Conditions

Stage 4A is feasible only when all three native backends, the deadline handoff
and all four mutations pass on one exact head. Production Stage 4 remains
separately deferred even after that result.

Any required primitive that is unavailable or ambiguous, any need for PID/PPID
correctness, a renewed deadline, weakened Stage 2 owner death, an unbounded
helper, a persistent service or Stage 5 work stops this checkpoint.

## Commit And Rollback

Keep three independent review boundaries:

1. `docs: record restart handoff design conflict`;
2. `test: prove restart handoff feasibility`;
3. `docs: record restart handoff feasibility result`.

Rollback removes only the Stage 4A fixture and these documentation deltas.
Existing Stage 2 and Stage 3 production behavior remains unchanged.
