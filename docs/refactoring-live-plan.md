# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-29
Current group: Gate 9 large-owner convergence
Current branch: `refactor/aria-client-provenance`

This file contains only unfinished or not-yet-integrated work. Completed PR 02-32 items are not restored. Design rationale belongs in `design-docs`; product acceptance belongs in `product-specs`.

## State Correction

The previous `Status: complete` was incorrect.

- `origin/refactor/pr-30-32-release-hardening` is not an ancestor of `origin/main`.
- PR #78 was merged into the stacked base `refactor/pr-25-29-remove-legacy`, not into `main`.
- PR #75 and PR #77 are closed after their replacement was validated in PR #82.
- PR #79 and PR #80 were superseded by green PR #83, closed, and their typed replacement was merged into the stacked release-hardening base.
- Gate 4 passed Windows/Linux/macOS quality CI and CodeQL, then PR #87 was merged into `refactor/pr-30-32-release-hardening` as merge commit `d8342abc`.
- Gate 5 and the authenticated read-only Bilibili audit passed Windows/Linux/macOS quality CI and CodeQL, then PR #88 was merged into `refactor/pr-30-32-release-hardening` as merge commit `fadd7eb3`.
- The authenticated audit was repeated on 2026-07-28: its `/nav` login gate and all 14 contract probes passed with zero drift. Only the allowlisted sanitized diagnostics artifact is retained; Gitleaks scanned 934 candidate files and reported zero findings.
- Gate 6 stage extraction passed two complete Windows/Linux/macOS quality and CodeQL rounds, then PR #89 was merged into `refactor/pr-30-32-release-hardening` as merge commit `e288913f`.
- Gate 6 retry policy passed three complete remote rounds. Final Windows/Linux/macOS quality run `30187455431` and CodeQL run `30187455441` had zero check annotations, then PR #90 was merged into `refactor/pr-30-32-release-hardening` as merge commit `ba0a928e`.
- Gate 7 async Bilibili Infrastructure ownership passed Windows/Linux/macOS quality run `30189537538`, protobuf run `30189537553`, and CodeQL run `30189537541`, then PR #91 was merged into `refactor/pr-30-32-release-hardening` as merge commit `55070903`.
- Gate 8 passed Windows/Linux/macOS quality run `30191251004`, protobuf run `30191250997`, and CodeQL run `30191250992`; PR #92 was merged into `refactor/pr-30-32-release-hardening` as `f8e78c9a`. CodeQL reported no alert, but GitHub emitted one platform annotation because the single required ownership PR changed 396 files and its diff API is capped at 300 files.
- Gate 9 logging ownership passed Windows/Linux/macOS quality run `30345373830`, protobuf run `30345371405`, and CodeQL run `30345371181`, with zero check annotations. PR #94 was merged into `refactor/pr-30-32-release-hardening` as merge commit `b290b204`.
- Gate 9 naming convergence passed Windows/Linux/macOS quality run `30347937643` and CodeQL run `30347937639`; all seven check-runs had zero annotations. PR #95 was merged into `refactor/pr-30-32-release-hardening` as merge commit `e29ecc8`. Generic-name and file/type-mismatch baselines are zero; the four remaining duplicate-name sets are endpoint/role-scoped contracts.
- Gate 9 SQLite store ownership passed Windows/Linux/macOS quality run `30349573925` and CodeQL run `30349573991`; all seven check-runs had zero annotations. PR #96 was merged into `refactor/pr-30-32-release-hardening` as merge commit `9f570f4`. The Store fell from 928 to 447 lines with byte-content-equivalent SQL literals and unchanged schema/resume contracts.
- The authenticated read-only API audit was refreshed against `9f570f4`; all 14 probes passed with zero drift, Gitleaks found zero secrets, and all seven remote checks had zero annotations. PR #97 was merged into `refactor/pr-30-32-release-hardening` as merge commit `3394ff5`.
- Gate 9 Settings network/aria ownership passed Windows/Linux/macOS quality run `30351528461` and CodeQL run `30351528583`; all seven check-runs had zero annotations. PR #98 was merged into `refactor/pr-30-32-release-hardening` as merge commit `ffa5674`. The 671-line mixed partial became 319-line general network and 355-line aria runtime owners with all 44 public compatibility methods unchanged.
- Gate 9 network-settings ViewModel ownership passed Windows/Linux/macOS quality run `30352739481` and Analyze/CodeQL run `30352739315`; all seven check-runs had zero annotations. PR #99 was merged into `refactor/pr-30-32-release-hardening` as merge commit `660d223`. The 649-line mixed owner became 384-line navigation/general-command, 275-line aria-command, and 292-line binding-state owners with XAML binding names unchanged.
- Gate 9 video-settings ViewModel ownership passed Windows/Linux/macOS quality run `30354918725` and CodeQL run `30354918709`; all seven check-runs had zero annotations. PR #100 was merged into `refactor/pr-30-32-release-hardening` as merge commit `e663281`. The 1,020-line mixed owner became 451-line navigation/playback/transcoding, 353-line content/naming-command, and 248-line binding-state owners. The first Windows run exposed a declaration-regex timeout; line-scoped non-backtracking matching and an adversarial regression test fixed it before merge.
- Gate 9 my-space ViewModel ownership passed Windows/Linux/macOS quality run `30356078414` and CodeQL run `30356078409`; all seven check-runs had zero annotations. PR #101 was merged into `refactor/pr-30-32-release-hardening` as merge commit `11ee968`. The 669-line owner became a 408-line navigation/profile workflow owner and a 265-line service-free binding-state owner without changing typed navigation, cancellation or XAML binding contracts.
- The authenticated read-only API audit was repeated against `11ee968`; the isolated process reloaded the credential from `~/.codex/.env`, the `/nav` hard gate passed, and all 14 allowlisted probes passed with zero contract drift. Gitleaks inspected 939 candidate files and reported zero findings. Strict PR CI run `30357290660` and CodeQL run `30357290313` completed seven successful checks with zero annotations; PR #102 was merged into `refactor/pr-30-32-release-hardening` as merge commit `cc8a9ca`.
- Gate 9 add-to-download ownership passed Windows/Linux/macOS quality run `30359317685` and CodeQL run `30359317951`; all seven check-runs had zero annotations. PR #103 was merged into `refactor/pr-30-32-release-hardening` as merge commit `a00812e`. The 663-line mixed owner became a 275-line session coordinator plus dedicated duplicate, stateless draft and optional metadata owners without changing settings snapshots, task shape, cancellation or admission.
- Gate 9 user-space ViewModel ownership passed Windows/Linux/macOS quality run `30360515743` and CodeQL run `30360513188`; all seven check-runs had zero annotations. PR #104 was merged into `refactor/pr-30-32-release-hardening` as merge commit `a946242`. The 569-line mixed owner became a 412-line typed-navigation/load/projection workflow owner, a 161-line service-free binding-state owner and the unchanged 27-line favorite-folder owner.
- Gate 9 bangumi-follow ViewModel ownership passed Windows/Linux/macOS quality run `30364267364` and CodeQL run `30364266723`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #105 was merged into `refactor/pr-30-32-release-hardening` as merge commit `1e265d1`. The 531-line mixed owner became a 435-line pager/navigation/load/download workflow owner and a 103-line service-free binding-state owner. CI also exposed and fixed NLog target-batch rotation overshoot plus solution-level xUnit host/TRX contention before merge.
- Gate 9 input-parser ownership passed Windows/Linux/macOS quality run `30366101959` and CodeQL run `30366101939`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #106 was merged into `refactor/pr-30-32-release-hardening` as merge commit `152e4a4`. The 586-line mixed parser became eight responsibility partials below 120 lines, with deterministic canonical, sentinel, null and exact-host contracts.
- Gate 9 search composition passed Windows/Linux/macOS quality run `30367302324` and CodeQL run `30367302508`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #107 was merged into `refactor/pr-30-32-release-hardening` as merge commit `dd9a81a`. MainWindow and Index now share the Host-owned `SearchService`, and an architecture ratchet rejects direct ViewModel construction.
- Gate 9 pager ownership passed Windows/Linux/macOS quality run `30369076250` and CodeQL run `30369076284`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #108 was merged into `refactor/pr-30-32-release-hardening` as merge commit `59e7a1c`. The 506-line pager became focused state/command/layout owners, and parameterless XAML buttons plus constructor current-page behavior were repaired.
- Gate 9 network-settings View ownership passed Windows/Linux/macOS quality run `30370919469` and CodeQL run `30370919558`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #109 was merged into `refactor/pr-30-32-release-hardening` as merge commit `e181659`. The 608-line XAML became a thin ordered composition plus four typed section owners while retaining every binding, named control, resource, and command parameter.
- Gate 9 video-detail View ownership passed Windows/Linux/macOS quality run `30423173188` and CodeQL run `30423172978`; all seven check-runs had zero annotations and every platform retained seven assembly-named TRX artifacts. PR #110 was merged into `refactor/pr-30-32-release-hardening` as merge commit `bb082a7`. The 565-line XAML became a thin ordered composition plus toolbar, summary, same-namescope section/page selection and action owners. The first remote round exposed a macOS child-view static-resource lookup and Windows global smoke-theme mutation; the final design keeps the application-owned DataGrid theme and uses local row-highlight styles without replacing global test state.
- `version.txt` remains `1.0.32`; v1.1.0 has not passed its release gate.

No release tag may be created while any release blocker below remains.

## Execution Order

### Gate 9: Large-Owner Convergence

Owner branches: separate responsibility-based large-owner PRs.

Scope:

- Split hand-written oversized owners by responsibility; do not split generated/protocol files only to satisfy LOC.
- Current owner: classify the source and synchronization contract of `AriaClient`, then converge the final oversized production file without changing its public API or aria2 JSON-RPC wire contract.

Current owner progress, pending PR integration:

- Git history traces the source to DownKyi commit `587fcfb` (`add the aria2cNet sources`, 2020-12-26). No generator, external package, or separate upstream sync source exists; it is hand-maintained project protocol code.
- The 1,119-line owner is split into transport core, download control, status/URI, options, lifecycle and `system.*` partials, all below 500 lines, without changing public signatures.
- A deterministic contract inventory invokes every public RPC method and verifies JSON-RPC version, request ID, method name and token placement. It exposed and fixed the pre-existing `ChangeUriAsync` mapping to `aria2.changePosition`; the method now correctly emits `aria2.changeUri`.
- Strict `AnalysisMode=All` Release build has zero warnings/errors and all 713 tests pass across seven test projects. Format changed 0/849 files; the boundary audit reports zero oversized production files; vulnerable/deprecated package audits, `git diff --check`, and the 985-candidate Gitleaks scan pass.
- PR #111 implementation head passed Windows/Linux/macOS quality run `30424513258` and CodeQL run `30424513284`; all seven checks had zero annotations and every platform retained seven distinct assembly-named TRX artifacts. The final documentation head must repeat the remote gates before integration.

Verification:

- owner-specific contract and behavior tests pass before the full solution gate.
- oversized-file ratchet entries decrease and no new entries are added.

Completion:

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
