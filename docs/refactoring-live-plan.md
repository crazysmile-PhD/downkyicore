# DownKyi Live Plan

Status: active
Last updated: 2026-08-08
Current work item: unblock and integrate PR #120 -> #126 -> #124 -> #125
Current branch: `fix/download-output-path-ownership`
Current base: frozen PR #120 head `a2119be346757a48c46b0beadacad6a4426bc031`

This file contains only unfinished, blocked or integration-pending work. Detailed
contracts live in `docs/exec-plans/`; stable completed facts belong in architecture,
maintenance and release documentation. A finding is not complete until its behavior,
tests and required gates are green on the exact commit.

## Integration Queue

- [ ] PR #120 remains frozen at `a2119be346757a48c46b0beadacad6a4426bc031`; external
      contribution checks require maintainer approval.
- [ ] PR #126 (`539d4aa`) replaces the flaky Windows ready-file deadline with a deterministic
      child-process handshake. Its complete exact-head matrix is green.
- [ ] PR #124 (`369a171`) contains only dialog/startup behavior above #126. Its complete
      exact-head matrix is green.
- [ ] PR #125 (`662765a`) contains only P0 output-path ownership above #124. Its complete
      exact-head matrix is green.
- [ ] Merge in dependency order, retarget each next PR to `main`, rerun exact-head checks after
      every retarget, and remove the corresponding completed item only after integration.

## Mandatory Pre-Implementation Audit

- [x] Compare every proposed class, service, test and flow with existing owners before editing.
- [x] Remove parallel implementations when an existing owner already covers the responsibility.
- [x] Do not push PR #120 until the current unpushed diff has passed this overlap audit.
- [x] Record unresolved product-semantic conflicts instead of silently keeping both paths.

Current overlap checks:

- [x] Keep `DownloadFileIntegrity` as the single active-download file validator; completed history
      and `RepeatDownloadStrategy` remain independent of current file-system availability.
- [x] Reuse `CustomPagerViewModelTests.NavigationButtonsExecuteWithoutCommandParameters`; no
      second pager command suite remains.
- [x] Restore the existing cancellation-aware restart helper and `WaitForExitAsync` contract; the
      synchronous wait was not required by a new failure and contradicted lifecycle policy.
- [x] Audit the remaining changes: search extends its existing command, watch-later reuses the
      existing range collection, SQLite cleanup extends the existing store, and dynamics reuses
      the existing platform launcher.

## Current Item: PR #120 Scope Cleanup

Detailed contract: `docs/exec-plans/v1.1.1-pr-120-scope-cleanup.md`.

### Remove Rejected Or Superseded Work

- [x] Remove history auto refresh and its settings, UI and tests.
- [x] Remove native Bilibili dynamics API/page/route and associated tests.
- [x] Remove aria2 error-code 1 special retry and fixed global retry expansion.
- [x] Remove rejected file-system-aware duplicate behavior and its tests.
- [x] Remove duplicate pager regression tests and unrelated formatting drift.

### Keep And Complete Approved PR #120 Work

- [x] Open official Bilibili dynamics in the system browser; keep internal personal-space routes typed.
- [x] Keep watch-later list virtualization and atomic batch replacement without clearing old data first.
- [x] Keep the home search command truly parameterless.
- [x] Reuse the existing pager regression suite rather than maintaining two equivalent tests.
- [x] Preserve the existing async restart-helper and STA entry contracts without a blocking wait.
- [x] Transactionally delete only orphaned `downloading` rows during both current-schema and legacy initialization.
- [x] Make `DownloadArtifactsStage` participate in typed pipeline failure: cover, subtitle and
      danmaku execution failures must stop `FinalizeStage`; no-resource outcomes remain distinct
      from HTTP, malformed protobuf/JSON, conversion, permission, write and zero-byte failures.

### PR #120 Exit Conditions

- [x] `main...HEAD` contains only approved work and no rejected keyword/path residue.
- [x] Strict Release build, all seven test projects, format, architecture, lifecycle, package,
      workflow and secret gates pass.
- [x] Update PR body to the exact final diff and push without rebase or force-push.
- [ ] PR #120 remains frozen at `a2119be346757a48c46b0beadacad6a4426bc031`; its external
      contribution checks still require maintainer approval before exact-head CI can complete.

## Ordered Runtime Hardening Backlog

Detailed contract: `docs/exec-plans/v1.1.1-runtime-hardening.md`.

### P0 Data Ownership

1. [ ] Atomically reserve normalized output base paths at task admission across queued,
       downloading and paused tasks; use case-insensitive comparison on Windows and reject
       unowned overwrite at final move.
       Local formal Verification and PR #125 exact-head cross-platform CI are green; keep this item
       open until the dependency stack is integrated into `main`.
2. [ ] On source-media validation or mux failure, revoke only invalid completed transfer keys,
       clear their partial/resume state and backend identity, and preserve source files for
       infrastructure-only failures.

### P1 Recovery And Media Correctness

3. [ ] Prevent unverified cross-origin CDN resume; changing source requires compatible
       validators or a clean restart.
4. [ ] Treat `OperationCanceledException` as normal only when the owning token is canceled;
       unexpected cancellation must produce a retryable failed task, never a stuck Downloading task.
5. [ ] Move post-transfer integrity into the retry loop so invalid aria2/builtin output can use
       backup addresses and one bounded playback-address refresh.
6. [ ] Tighten media-duration tolerance with a fixed upper bound and validate both actual and
       expected tails.
7. [ ] Require and validate audio streams when the selected output requires audio; decode audio
       near the tail and compare audio/video durations.

### P2 Defensive Completeness

8. [ ] Reject oversized files when a reliable exact expected length is available.
9. [ ] Add FFmpeg fail-on-error behavior and multi-point/segment-boundary decode checks without
       turning normal PR verification into an unstable full benchmark.
10. [ ] Make DURL output honor audio-only/video-only selection and transcoding semantics.
11. [ ] Define and test no-subtitle/no-cover product semantics separately from execution failure;
        NFO write failures must not be hidden when metadata output is required.
12. [ ] Reject non-positive, duplicate or structurally invalid DURL order identities before transfer.

## Naming And Responsibility Clarity

- [ ] Audit and, where behavior is unchanged, rename update/migration entry points to express
      their separate responsibilities: `CheckForUpdatesAsync`, `OpenReleasePageAsync`,
      `MigrateLegacyUserDataAsync`, and `UpgradeDatabaseSchemaAsync`.
- [ ] Preserve existing settings, legacy-user-data and SQLite migration behavior; this is naming
      clarification, not a new upgrade framework.

## Later v1.1.1 Items

1. Bangumi `ep_id` propagation and playback response contract.
2. Remaining P1, merge-blocking or core runtime review debt.
3. Final release rehearsal, cross-platform packages and v1.1.1 publication.

## Release Blockers

- Every item above that is classified P0, P1, merge-blocking or release-blocking must be complete
  and integrated into current `main`.
- The exact final commit must pass strict quality, CodeQL, Main lifecycle 50, complete release
  rehearsal and Windows/Linux/macOS package validation.
- Settings JSON, legacy SQLite, unfinished tasks, GID, partial-file maps, completed keys and
  resume fixtures must remain compatible.
- `v1.1.0` stays immutable. Do not change `version.txt`, tag or publish v1.1.1 until every blocker is green.

## Verification

Run sequentially in one worktree:

```powershell
dotnet restore ./DownKyi.sln
pwsh ./script/validate-release-version.ps1
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:EnableNETAnalyzers=true -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true -p:UseSharedCompilation=false
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release -Iterations 5 -NoBuild -ValidateForensics `
  -ResultsDirectory ./artifacts/assembly-lifecycle/verification
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml | `
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
git diff --check
```

A result is valid only when runtime, OS, architecture, commit SHA and dirty-worktree state are recorded.

Gate 10 is complete only when lifecycle ownership audit and repeated isolated-process checks both
pass with their required machine-readable evidence; a single successful solution test run is not
a substitute for the lifecycle gate.

## Completion And Rollback

An item is complete only after implementation, deterministic tests, documentation, exact-head CI
and review are green. Roll back by reverting that item's semantic commit range without changing
user data formats or reintroducing a security bypass. Completed items are removed from this live
file and their stable contracts are promoted to architecture and maintenance documentation.
