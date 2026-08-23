# AGENTS.md - DownKyi Agent Entry

This file is a small repository map and guardrail. Do not read every linked
document by default. Start from the current task, inspect the affected code and
tests, then open only the subsystem documentation needed to make and verify the
change.

## Work Continuity

- The owner-only Codex workboard is GitHub Issue
  [#137](https://github.com/crazysmile-PhD/downkyicore/issues/137). It contains
  only bookmarks and short interruption checkpoints for work the owner asked
  Codex to do.
- Load the selected bookmark and its linked PR or task document. Do not scan
  community Issues or contributor PRs unless the owner explicitly assigns one.
- When interrupted, update only the short checkpoint in #137. When work is
  complete, remove its bookmark. Do not keep a completed-work list.
- Product PRs must not edit `docs/refactoring-live-plan.md` to record Current
  Item, Next Item, branch, SHA, CI state or progress. That file owns stable
  release and verification policy only.
- Scope containment does not require branch dependency containment. Keep
  separately reviewable root causes as separate commits or evidence, but stop
  extending an unmerged release stack after roughly two or three dependency
  layers, or once it materially diverges from `main`. Rebuild one clean
  current-main integration branch and validate its exact head; do not invent a
  registry, label system or workflow framework to manage stack growth.

## Progressive Disclosure Map

- Current architecture and dependency direction: `ARCHITECTURE.md`.
- Detailed node ownership and test anchors: `docs/ai-knowledge-graph.md`.
- Release and formal local verification policy: `docs/refactoring-live-plan.md`
  and `docs/operations/verification-and-rollback.md`.
- Bilibili endpoints, WBI and JSON contracts:
  `docs/operations/bilibili-api-audit.md`.
- Review findings, invariant derivation and scope containment:
  `docs/testing/review-invariant-policy.md` and
  `docs/testing/review-invariant-corpus.json`.
- Thread, process, Host, Dispatcher or test-fixture teardown:
  `docs/testing/assembly-lifecycle-stability.md`.
- External binaries, dependencies and release maintenance:
  `docs/maintenance.md`.
- Accepted target designs: `docs/design-docs/`; task-specific execution plans:
  `docs/exec-plans/`; product behavior: `docs/product-specs/`.

Open the relevant entry only when the task touches that domain. Stable current
truth belongs in architecture documents; target designs and baseline snapshots
must not be reported as already implemented.

## Architecture Guardrails

- `DownKyi` is the minimal executable. Avalonia composition and UI runtime live
  in `src/DownKyi.Desktop`; use cases/contracts in `src/DownKyi.Application`;
  durable adapters in `src/DownKyi.Infrastructure`; state rules in
  `src/DownKyi.Domain`; Bilibili and compatible media runtime remain in
  `DownKyi.Core` until deliberately migrated.
- Use Microsoft DI, typed navigation/dialog contracts and CommunityToolkit
  MVVM. Do not reintroduce Prism, DryIoc, EventAggregator, RegionManager,
  ContainerLocator, service locators or a second router/container.
- Inspect existing modules and tests before adding a class or service. Extend
  the authoritative owner; do not create parallel state, validation, retry,
  mapping, registry or persistence owners.
- Preserve settings JSON, SQLite migrations, download history, unfinished
  tasks, GID, transfer files, completed keys and resume compatibility unless
  the task explicitly changes a tested contract.
- Keep cancellation semantic. Do not use `.Result`, `.Wait()`, blocking sleeps,
  silent catches, empty/null success sentinels, unobserved fire-and-forget work
  or destructive cleanup before the workflow commit boundary.
- Logs and evidence must exclude cookies, tokens, full sensitive URLs, account
  identifiers and complete personal paths.

## Review Remediation Gate

- A review finding is symptom evidence, not a patch instruction. Identify the
  violated invariant, trace the complete failure path, search sibling paths and
  repair the earliest owner that lost semantics or made the wrong transition.
- If result taxonomy, cleanup, commit boundary, ownership or transaction design
  is the root cause, repair the shared abstraction. Do not layer caller-specific
  `if`, catch or sentinel patches.
- If the same failure family reappears in the same PR, **停止 local patch** and
  re-evaluate the typed result, state machine, owner and transaction boundary.
- Investigation may widen evidence, but **不能自動擴大目前 PR 的修改範圍**.
  A different invariant goes to the owner-requested backlog 或 separate PR;
  do not opportunistically add it to the active change.
- Follow `finding -> root cause -> invariant -> sibling-path search ->
  generator/state space -> adversarial proof -> production fix`. Derive
  regression tests from the invariant or external protocol; a single example
  is only a counterexample when the failure family has a generatable state
  space. Retry, cleanup, lifecycle and persistence work needs a
  failure/transition matrix.
- Any operation-created file must be recorded in durable task state before its
  first write, or be observably removed before the operation returns. Do not
  leave physical output without a durable owner or add a second path registry.
- Important invariant gates need an adversarial or mutation fixture proving an
  intentionally broken owner, transition or contract makes CI fail. Checking
  only for a source string does not establish a fail-closed gate.

## Change Locality

- A large changed-file count is an investigation signal, not proof of debt.
  Distinguish legitimate separation of concerns from duplicated authoritative
  identity, mapping, policy or state.
- When several places manually describe the same fact, identify one owner and
  derive the rest. Do not add another registry or synchronization checklist.
- Update `docs/ai-knowledge-graph.md` only when current ownership or dependency
  direction changes. Keep temporary status and branch history out of it.

## Verification

Use the smallest focused test while iterating. Before push, run the formal
commands in `docs/refactoring-live-plan.md` sequentially in one worktree. At
minimum, behavioral changes require strict Release build, all seven test
projects, review invariants, format and `git diff --check`; lifecycle/process
changes also require the documented ownership and repeated process gates.

Do not weaken analyzers, architecture tests, lifecycle gates, secret scanning
or platform checks to make a change green. A passing build alone does not prove
runtime behavior.
