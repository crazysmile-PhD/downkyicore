# AGENTS.md - DownKyi Agent Entry

This file is a small repository map and guardrail. Do not read every linked
document by default. Start from the current task, inspect the affected code and
tests, then open only the subsystem documentation needed to make and verify the
change.

## Work Continuity

- The sole work-management entry is
  [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2).
  Its fields and ordering own Priority, Status, items awaiting verification or
  a decision, Draft items, In Progress and Done.
- Start from the Project item selected by the owner and load its linked Issue,
  PR or task document. Do not scan community Issues or contributor PRs for
  work unless the owner explicitly assigns them through the Project.
- A GitHub Issue owns one problem, requirement, research question or decision,
  including its background, scope, evidence, acceptance criteria and PR links.
  Work without an Issue must be a Project Draft item; do not encode it as a
  section in another Issue or maintain the same list in repository documents.
- When interrupted, update the Project Status and keep durable implementation
  evidence in the linked Issue or PR. When complete, set the Project item to
  Done; do not manually remove it or create a second completed-work list.
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
- Desktop wiring starts at `src/DownKyi.Desktop/Composition/DesktopComposition.cs`;
  follow its local composition call into the affected module, then inspect that
  module's contracts, constructors and focused tests.
- Release and completion policy: `docs/refactoring-live-plan.md`; formal local
  commands and rollback procedure: `docs/operations/verification-and-rollback.md`.
- Bilibili endpoints, WBI and JSON contracts:
  `docs/operations/bilibili-api-audit.md`.
- Test projects, the formal runner and failure diagnostics:
  `docs/testing/README.md`.
- External binaries, dependencies and release maintenance:
  `docs/maintenance.md`.
- Accepted non-derived decisions: `docs/design-docs/`; active work:
  [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2);
  user-facing behavior: `README.md`; release history: `CHANGELOG.md`.

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
  production fix`. Add the smallest behavioral regression that reproduces the
  real failure and proves the repaired owner; do not build a verifier for a
  hypothetical bypass.
- Any operation-created file must be recorded in durable task state before its
  first write, or be observably removed before the operation returns. Do not
  leave physical output without a durable owner or add a second path registry.

## Change Locality

- A large changed-file count is an investigation signal, not proof of debt.
  Distinguish legitimate separation of concerns from duplicated authoritative
  identity, mapping, policy or state.
- When several places manually describe the same fact, identify one owner and
  derive the rest. Do not add another registry or synchronization checklist.
- Keep mechanically derivable wiring in composition code and tests, not in a
  manually synchronized module or call graph. Keep temporary status and branch
  history out of current architecture documentation.

## Verification

Use the smallest focused test while iterating. Before push, run the formal
commands in `docs/operations/verification-and-rollback.md` sequentially in one
worktree. At minimum, behavioral changes require strict Release build, the
applicable test projects through `DownKyi.CentralTestRunner`, format and
`git diff --check`. Process failures retain the runner's lightweight
flight-recorder evidence.

Do not weaken analyzers, relevant architecture tests, secret scanning or
platform checks to make a change green. A passing build alone does not prove
runtime behavior.
