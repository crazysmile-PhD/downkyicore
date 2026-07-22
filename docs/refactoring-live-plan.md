# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-22
Current group: Gate 3 Bilibili API contract audit
Current branch: `audit/gate-03-bilibili-api-contracts`

This file contains only unfinished or not-yet-integrated work. Completed PR 02-32 items are not restored. Design rationale belongs in `design-docs`; product acceptance belongs in `product-specs`.

## State Correction

The previous `Status: complete` was incorrect.

- `origin/refactor/pr-30-32-release-hardening` is not an ancestor of `origin/main`.
- PR #78 was merged into the stacked base `refactor/pr-25-29-remove-legacy`, not into `main`.
- PR #75 and PR #77 are closed after their replacement was validated in PR #82.
- PR #79 and PR #80 were superseded by green PR #83, closed, and their typed replacement was merged into the stacked release-hardening base.
- `version.txt` remains `1.0.32`; v1.1.0 has not passed its release gate.

No release tag may be created while any release blocker below remains.

## Execution Order

### Gate 3: Bilibili API Inventory And Runtime Contract Audit

Owner branch: `audit/gate-03-bilibili-api-contracts` from the latest integrated architecture head.

Progress (2026-07-22):

- Inventoried all 47 fixed Bilibili endpoints in Core with method, envelope, authentication/WBI requirement, runtime use, evidence, alternative, decision and tests.
- Added an explicit anonymous live-probe script that records Runtime, OS, architecture and Commit SHA while refusing to load local cookies or account state.
- Confirmed and fixed anonymous WBI bootstrap: `/x/web-interface/nav` returns `-101` with valid public WBI metadata, so only this endpoint may deserialize that code.
- Cross-checked maintained yt-dlp, bilibili-api endpoint maps, community protocol docs and controlled live probes.
- Migrated bangumi playback from the working legacy v1 route to current v2 and made `result.video_info` a nullable required contract.
- Recorded retired channel APIs, the invalid unused nickname query, legacy danmaku and authenticated watch-later ambiguity without speculative remapping.
- Full local gates are green: strict Release build `0 warning / 0 error`, all `543/543` tests, format `0/742` changed files, clean vulnerable/deprecated package audits, module-boundary inventory, anonymous live-probe `27/27` HTTP results, and `git diff --check`.
- Remaining Gate 3 work is commit/PR publication, remote Windows/Linux/macOS CI, review, and merge into the stacked release-hardening base.

Scope:

- Inventory every Bilibili endpoint, method, envelope field, authentication/WBI requirement and caller.
- Cross-check recent reliable documentation, at least one maintained open-source implementation and controlled live requests.
- Replace an endpoint only when evidence is strong; otherwise record risk without speculative change.
- Add fixed JSON fixtures and injectable/loopback HTTP tests for changed contracts.
- Keep cookies, tokens, personal paths and sensitive URLs out of reports and logs.

Verification:

- Report lists endpoint, use, status, evidence, alternative, code change and test.
- ordinary video, bangumi, cheese, list, favorites, publication, login and user-space paths are covered.
- no unit test depends on production Bilibili availability.

Completion:

- API audit PR is green and merged.
- Every changed endpoint has a regression fixture and typed failure behavior.

Rollback:

- Revert endpoint changes independently; retain audit evidence and risk notes.

### Gate 4: Make Domain DownloadTask The Runtime Authority

Owner branch: `refactor/domain-download-authority`.

Scope:

- Commands address tasks by `DownloadTaskId`.
- Load aggregate, invoke legal transition, persist, then publish projection changes.
- Remove UI model -> Domain reconstruction as the normal write path.
- Preserve legacy SQLite/JSON readers only at migration adapters.
- Preserve unfinished tasks, GID, partial file map, completed segment keys and optimistic version.

Verification:

- state transition tests cover start, pause, resume, fail, complete, cancel and shutdown recovery.
- legacy database fixtures migrate and reopen without data loss.
- architecture tests reject `DomainDownloadTask.Restore` outside migration/store adapters.

Completion:

- worker/pipeline APIs no longer accept `DownloadingItem` as task authority.
- `CreateUnfinishedTask`, `ToLegacyStatus` and reverse Domain mapping are removed from runtime flow.

Rollback:

- Keep a feature-compatible adapter commit boundary so runtime authority can be reverted without reverting schema compatibility.

### Gate 5: Replace UI Polling With Event-Driven Enqueue

Owner branch: `refactor/event-driven-download-queue` after Gate 4.

Scope:

- Add `EnqueueAsync(DownloadTaskId, CancellationToken)`.
- Restore queued/interrupted task IDs from SQLite once during startup.
- Give each active task an explicit cancellation owner.
- Remove `DispatchAsync`, 500 ms polling, `_queuedDownloads` and collection-membership validity checks.

Verification:

- enqueue latency tests use deterministic clock/channel controls.
- 1/4/8 worker tests prove no duplicate execution.
- shutdown cancellation restores resumable state.

Completion:

- runtime never scans an `ObservableCollection` for work.

Rollback:

- Revert queue wiring only after confirming stored queued tasks are still discoverable on next launch.

### Gate 6: Split DownloadPipeline And Centralize Retry

Owner branches: one stage extraction PR, followed by one retry-policy PR.

Scope:

- Introduce `DownloadExecutionContext` and typed stage results.
- Extract resolve, media transfer, artifacts, mux, validate and finalize stages.
- Move localized UI text to Desktop presenter.
- Establish one retry budget owner with typed decisions for timeout/5xx, 429, expired URL, invalid media, disk error and cancellation.

Verification:

- each stage has deterministic unit tests.
- fake HTTP tests cover interrupted, empty, wrong length, 403, 429, 500 and slow responses.
- retry-count tests prove no multiplicative pipeline x backend attempts.

Completion:

- pipeline only orders stages.
- no stage references `DictionaryResource`, UI collection or ViewModel types.

Rollback:

- Each extracted stage is one commit and can be reverted without changing stored task format.

### Gate 7: Finish HTTP And Infrastructure Ownership

Owner branch: `refactor/async-bilibili-infrastructure`.

Scope:

- Define injected `IBilibiliApiClient`, `IBuvidProvider` and existing `IWbiKeyProvider` ports.
- Move implementation to Infrastructure.
- Use `SendAsync`, async content/stream reads and `Task.Delay`.
- Remove static `WebClient` state and `Configure()`.
- Move aria2, FFmpeg, file system and logging sink configuration toward Infrastructure in test-protected steps.

Verification:

- cancellation during request/backoff propagates immediately.
- retry exhaustion preserves typed HTTP/API error.
- all Bilibili fixtures remain green.
- architecture tests reject static client facades and sync network IO.

Completion:

- Application and Desktop depend on ports, not Core static facades.

Rollback:

- Keep endpoint adapters behavior-compatible until all callers migrate; remove facade only in the final commit.

### Gate 8: Complete Desktop Boundary And UI Projection Ownership

Owner branch: `refactor/desktop-boundary` after runtime ports stabilize.

Scope:

- Move Views, ViewModels, UI projections, navigation/dialog adapters, dispatcher and lifecycle to `DownKyi.Desktop`.
- Keep executable as minimal startup/composition.
- Replace `ImmutableObservableCollection<T>` with owner-only `ObservableCollection<T>` exposed as `ReadOnlyObservableCollection<T>`.
- Move QR rendering and Core XAML resources to Desktop.
- Replace presentation-bound service contracts with Application DTO/ports.

Verification:

- Host smoke resolves full XAML and key ViewModels from the new Desktop assembly.
- collection contract and UI-thread tests pass.
- Core has no Avalonia dependency or `.axaml` resource.
- Application/service interfaces have no `DownKyi.ViewModels` types.

Completion:

- `DownKyi.Desktop` is the actual Desktop owner described in `ARCHITECTURE.md`.
- executable contains no runtime service, ViewModel or platform adapter implementation.

Rollback:

- Move by responsibility slice with rename maps; revert a slice as one commit if XAML/resource/DI smoke fails.

### Gate 9: Logging, Naming And Large-Owner Convergence

Owner branches: separate ADR/implementation, naming, and large-owner PRs.

Scope:

- Decide logging sink ownership through an ADR and benchmark before adding a dependency.
- Keep project-specific redaction before every persistence/cache/export path.
- Separate recent buffer, diagnostic exporter and retention responsibilities.
- Rename `Languanges`, QR/FFmpeg casing, duplicate SeasonsSeries owners and proven generic buckets in isolated rename PRs.
- Split hand-written oversized owners by responsibility; do not split generated/protocol files only to satisfy LOC.

Verification:

- redaction, flush, rotation, retention and shutdown tests remain green.
- all XAML/resource URI and typed route smoke tests pass after rename.
- module-boundary ratchet entries decrease and no new entries are added.

Completion:

- remaining naming exceptions have a documented protocol/generated-code reason.
- knowledge graph and architecture docs match final ownership.

Rollback:

- Revert each rename or owner extraction as an atomic commit.

### Gate 10: Integrate Main And Release v1.1.0

Owner branch: release branch from latest `main` only after Gates 1-9.

Scope and acceptance are defined in `product-specs/v1.1.0-release-gate.md`.

Completion:

- all required branches are integrated into latest `main`.
- Windows/Linux/macOS package validation is green for the same SHA.
- user data and resume fixtures pass.
- `version.txt` is changed once to `1.1.0` and all version consumers derive from it.
- clean `main` is tagged `v1.1.0` and the GitHub Release is published with verified artifacts/checksums.

Rollback:

- Never retag a different commit. If the release is invalid, publish a corrective version and document artifact withdrawal.

## Every-PR Checklist

- Read `AGENTS.md`, `ARCHITECTURE.md`, knowledge graph and this plan.
- State goal, scope, stable contracts, tests, completion and rollback in the PR.
- Add a test that fails on the old behavior when behavior changes.
- Preserve settings, SQLite, unfinished tasks and resume state.
- Update knowledge graph and live plan when ownership or dependencies change.
- Run strict build, full tests, format, diff and package audits sequentially.
- Do not add broad suppressions, restore legacy composition, or hide failure with null/empty sentinels.
