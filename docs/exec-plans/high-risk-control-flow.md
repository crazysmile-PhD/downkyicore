# High-Risk Control Flow And Transition Gate

Status: queued; do not start while the v1.1.1 upstream stack is unresolved

## Goal

Add two permanent CI defenses for orchestration and state-machine code without
refactoring unrelated production code merely to satisfy a metric:

1. A Roslyn syntax-based project-specific control-flow risk ratchet.
2. An invariant-derived transition matrix for download retry, cleanup, source,
   backend identity, persistence and finalization owners.

## Scope

- Define a documented project metric covering decisions, loops, catches,
  conditional expressions, boolean branching and nesting depth. Do not call it
  a standard cognitive-complexity metric.
- Measure current `main`; use only the minimum necessary baseline. New methods
  must obey a hard threshold, baseline methods cannot grow, and stale,
  duplicate or missing exemptions fail closed.
- Add adversarial Roslyn fixtures proving nesting raises risk, early return can
  reduce nesting risk, and names, formatting or receiver changes cannot bypass
  the rule.
- Start the semantic matrix at `DownloadTransferCoordinator`,
  `DownloadRetryPolicy` and sibling cleanup/source-switch owners. Cover corrupt,
  missing, inaccessible, runtime, timeout/transport, destination, cleanup,
  cancellation, pause/shutdown, same/next/refreshed source, and backend/resume
  identity outcomes.
- Register both proof classes in the existing review invariant corpus and
  runner. Do not create a second invariant runner.

## Guardrails

- A low syntax score does not prove semantic ownership is focused. If one
  method owns retry decisions, deletion, durable mutation and finalization,
  analyze the real owner/typed transition boundary rather than splitting it
  into cosmetic private helpers.
- Do not build rules around current file names, variable names, receivers or
  string markers.
- Keep deterministic architecture and transition proof in PR CI. Heavy race,
  process and real-binary evidence stays in Main or rehearsal.

## Acceptance

- The metric and baseline are measured, documented and self-tested.
- The transition matrix states retry, source contact, physical files,
  sidecars, completed keys, backend identity, destination mutation and durable
  task state for every failure class.
- At least five adversarial implementations are attempted. Any successful
  bypass is repaired before completion.
- Strict Release, all test projects, review invariants, architecture,
  lifecycle, format and diff gates pass.

## Rollback

Revert the gate commit as one unit. Do not keep a partial baseline without its
metric, adversarial fixtures and transition proof.
