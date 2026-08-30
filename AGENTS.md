# AGENTS.md - DownKyi Agent Entry

This file is the small repository map and guardrail. Start from the current
task, inspect the affected source and tests, then open only the relevant owner
below. Do not read every linked document by default.

## Work Continuity

- The owner-only Codex workboard is GitHub Issue
  [#137](https://github.com/crazysmile-PhD/downkyicore/issues/137). It contains
  bookmarks and short interruption checkpoints, not completed-work history.
- Load only the selected bookmark and its linked PR or task plan. Do not scan
  community Issues or contributor PRs unless the owner assigns one.
- When interrupted, update only the short checkpoint in #137. When work is
  complete, remove its bookmark.
- Keep separately reviewable root causes separate, but stop extending an
  unmerged release stack after roughly two or three dependency layers or
  material divergence from `main`. Rebuild one clean current-main integration
  branch and validate its exact head.

## Progressive Disclosure Map

- Architecture intent, compatibility commitments and dependency direction:
  [ARCHITECTURE.md](ARCHITECTURE.md).
- Topic-to-authority locator: [docs/ai-knowledge-graph.md](docs/ai-knowledge-graph.md).
- Formal local verification, release and rollback:
  [docs/operations/verification-and-rollback.md](docs/operations/verification-and-rollback.md).
- Bilibili endpoints, WBI and JSON contracts:
  [docs/operations/bilibili-api-audit.md](docs/operations/bilibili-api-audit.md).
- Review remediation and executable invariant mapping:
  [docs/testing/review-invariant-policy.md](docs/testing/review-invariant-policy.md)
  and [docs/testing/review-invariant-corpus.json](docs/testing/review-invariant-corpus.json).
- Thread, process, Host, Dispatcher and test-fixture teardown:
  [docs/testing/assembly-lifecycle-stability.md](docs/testing/assembly-lifecycle-stability.md).
- Process identity, tree containment, restart lifetime and supervision design:
  [docs/design-docs/process-lifecycle-ownership.md](docs/design-docs/process-lifecycle-ownership.md).
- Dependency and external-binary maintenance: [docs/maintenance.md](docs/maintenance.md).
- Accepted designs: [docs/design-docs/](docs/design-docs/); task-specific plans:
  [docs/exec-plans/](docs/exec-plans/); product commitments:
  [docs/product-specs/](docs/product-specs/).

Current work, branch, PR and CI state belong in GitHub. Target designs and
baseline snapshots must not be reported as already implemented.

## Documentation Policy

- Do not manually document facts that can be reliably derived from source,
  tests, configuration, workflows or machine-readable policy.
- Every non-derived stable fact has exactly one authoritative owner. Other
  documents link to that owner; they do not restate it.
- Before adding or generating documentation, prefer `DELETE`, then
  `LINK / QUERY ON DEMAND`, then `GENERATE`, and only then manual documentation.
- Current documentation contains only current intent, invariants, external
  contracts, compatibility commitments and necessary human procedures.
  Transient work state and historical execution evidence belong to GitHub or
  Git, not current-policy documents.
- `docs/ai-knowledge-graph.md` is a small locator of authoritative owners and
  high-value architecture boundaries, not a repository inventory or duplicate
  policy store.

## Architecture Guardrails

- `DownKyi` is the minimal executable. Avalonia composition and UI runtime live
  in `src/DownKyi.Desktop`; use cases and contracts in
  `src/DownKyi.Application`; durable adapters in `src/DownKyi.Infrastructure`;
  state rules in `src/DownKyi.Domain`; Bilibili and compatible media runtime
  remain in `DownKyi.Core` until deliberately migrated.
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

## Review And Change Locality

Follow [docs/testing/review-invariant-policy.md](docs/testing/review-invariant-policy.md)
for root-cause tracing, sibling-path search, state-space regression, mutation
proof and scope containment.

- A large changed-file count is an investigation signal, not proof of debt.
- Investigation may widen evidence but does not automatically widen the active
  PR. A different invariant belongs in the owner-requested backlog or a
  separate PR.
- When several places describe the same fact, identify one owner and derive or
  remove the rest. Do not add another registry or synchronization checklist.
- Important invariant gates need an adversarial or mutation fixture proving an
  intentionally broken owner, transition or contract makes CI fail.

## Verification

Use the smallest focused test while iterating. Before push, run the canonical
sequence in
[docs/operations/verification-and-rollback.md](docs/operations/verification-and-rollback.md)
sequentially in one worktree. The repository test runner discovers every test
project and selects the projects owned by the current OS; do not copy a fixed
project or test count into documentation.

Do not weaken analyzers, architecture tests, lifecycle gates, secret scanning
or platform checks to make a change green. A passing build alone does not prove
runtime behavior.
