# Download stability fixes summary (completed)

This document summarizes the download-stability audit findings that have already been addressed and merged.

Scope: documentation-only summary of completed work from PR #11 through PR #33.

Regression checklist: see `docs/maintenance/download-stability-regression-checklist.md` for ongoing manual verification of the completed batch.

## 1) Completed findings map

| Finding ID | Risk area | Status | Implemented in PR | Notes |
|---|---|---|---|---|
| DSA-01 | UI-thread safety for built-in progress updates | ✅ Completed | #16 | Progress updates are marshaled to the UI thread. |
| DSA-02 | UI-thread safety for aria2 progress updates | ✅ Completed | #16 | Progress updates are marshaled to the UI thread. |
| DSA-04 | FFmpeg failure recovery (temp input preservation) | ✅ Completed | #12 | Recoverable temp inputs are preserved when mux fails. |
| DSA-05 | Built-in downloader backup URL fallback | ✅ Completed | #11 | Backup URLs are attempted instead of failing immediately after one URL path fails. |
| DSA-06 | FFmpeg output overwrite safety | ✅ Completed | #13 | Final output overwrite behavior was hardened to avoid unsafe overwrite scenarios. |
| DSA-07 | Built-in downloader cancellation cleanup | ✅ Completed | #14 | Cancellation path cleanup was hardened for built-in downloads. |
| DSA-08 | aria2 cancellation cleanup | ✅ Completed | #15 | Cancellation path cleanup was hardened for aria2 downloads. |
| DSA-13 | FFmpeg concat list temp filename collision hardening | ✅ Completed | #22 | FFmpeg concat list temp filenames are collision-resistant. |
| DSA-14 | Download failure-context logging hardening | ✅ Completed | #19 | Download failure logging now includes richer contextual fields for diagnosability. |
| DSA-15 | NFO failure-path logging hardening | ✅ Completed | #20 | NFO generation failure path no longer silently swallows errors. |
| DSA-09 | Download path parsing hardening | ✅ Completed | #24 | Download path parsing is centralized and guarded. |
| DSA-10 | Concat temp-segment cleanup policy hardening | ✅ Completed | #26 | Successful concat temp-segment cleanup policy is clarified and guarded. |
| DSA-11 | Failed state persistence | ✅ Completed | #30 | `DownloadFailed(...)` now persists failed state immediately. |
| DSA-12 | Persistence collection snapshots | ✅ Completed | #31 | `DownloadFiles` and `DownloadedFiles` are snapshotted before persistence serialization. |
| Built-in resume branch | Resume-state handling for built-in downloader | ✅ Completed | #27 | Built-in downloader resume branch observes completion state. |
| Aria2 cleanup blocking | Cleanup path async behavior | ✅ Completed | #28 | Aria2 cleanup no longer uses synchronous waits in the cleanup path. |
| DSA-16 | aria2 completion handler diagnosability cleanup | ✅ Completed | #21 | Added completion-context logs in `AriaDownloadFinish()` without runtime behavior changes. |
| DSA-03 | FFmpeg mux-phase cancellation support | ✅ Completed | #33 | FFmpeg mux/concat phases now honor cancellation and classify cancelled outcomes distinctly from success/failure. |
| DSA-17 | built-in downloader memory-budget guardrail docs | ✅ Completed | #21 | Added inline comments documenting per-task memory budget and capacity planning formula. |

Related follow-up hardening (not a separate DSA row in the original table):

- PR #17 prevents blocking downloader callbacks while still performing UI-thread marshaling for progress updates.


## 1.1) Newly completed fixes since the last summary refresh

| Audit ID | Area | PR | Status | Notes |
|---|---|---|---|---|
| DSA-03 | FFmpeg mux-phase cancellation support | #33 | Completed | FFmpeg mux/concat phases now support cancellation classification distinct from failure/success. |

## 2) What changed (behavioral summary)

### DSA-05 — Built-in backup URL fallback (PR #11)

- Built-in download behavior now attempts backup URLs when the primary URL path fails.
- This reduces false-negative failures where media is available via alternate CDN/source URLs.

### DSA-04 — Preserve FFmpeg temp inputs on mux failure (PR #12)

- On mux failure, recoverable temp input media files are preserved.
- This improves retry/recovery by avoiding forced full redownload caused by premature cleanup.

### DSA-06 — Prevent unsafe FFmpeg output overwrite (PR #13)

- Output path handling was tightened to prevent unsafe overwrite behavior during merge/concat output creation.
- The goal is to reduce user data-loss risk when destination conflicts are detected.

### DSA-07 — Built-in cancellation cleanup (PR #14)

- Built-in downloader cancellation/stop paths now perform stronger cleanup and exit handling.
- This reduces orphaned or lingering in-flight transfer artifacts during cancellation.

### DSA-08 — aria2 cancellation cleanup (PR #15)

- aria2 cancellation path now performs stronger cleanup, aligning behavior with cancellation expectations.
- This reduces cases where remote/download state continues beyond intended cancellation.

### DSA-01 / DSA-02 — Marshal progress updates to UI thread (PR #16)

- Progress and related UI-bound download fields are marshaled onto the UI thread.
- This eliminates unsafe cross-thread UI property update patterns in both built-in and aria2 download modes.

### Callback responsiveness improvement (PR #17)

- Progress callback handling was adjusted to avoid blocking downloader callbacks while retaining UI-thread-safe updates.
- This reduces callback backpressure and potential throughput/latency regressions introduced by strict marshaling.

## Runtime fixes completed after the diagnostics batch

- PR #22 completed DSA-13 by making FFmpeg concat list temp filenames collision-resistant.
- PR #24 completed DSA-09 by centralizing download directory parsing and adding path guards.
- PR #26 completed DSA-10 by adding guarded cleanup for clearly owned concat temp segments after successful concat.
- PR #27 hardened the built-in downloader resume branch so resumed downloads update local completion/cancellation state.
- PR #28 reduced Aria2 cleanup blocking by replacing synchronous RPC waits with async best-effort cleanup while clearing stale gids for remove-task cleanup paths.
- PR #30 completed DSA-11 by persisting failed download state immediately in `DownloadFailed(...)`.
- PR #31 completed DSA-12 by snapshotting mutable per-item persistence collections before serialization.
- PR #33 completed DSA-03 by adding cancellation-aware FFmpeg mux/concat handling and distinct cancelled outcome classification.

## 3) What was intentionally **not** changed

To keep the above fixes narrow and low-risk, the completed PRs intentionally did **not** redesign broader runtime behavior such as:

- Download service architecture or scheduling model.
- FFmpeg feature set beyond targeted failure/overwrite stability fixes.
- aria2 feature model beyond targeted cancellation cleanup.
- WebClient retry strategy and global HTTP behavior.
- Database/settings schema and persistence model.
- Broad UI interaction model unrelated to progress-thread safety.

## 4) Remaining risks and follow-up recommendations

No remaining unresolved runtime-risk items from the original DSA table remain after PR #33.

Any future download-stability work should be treated as new findings or incremental hardening outside the original DSA closure scope.

## 5) Maintainer notes

- Treat PR #11–#33 as the completed stability batches to date, covering fallback, cancellation cleanup, overwrite protection, thread-safe progress signaling, path safety, concat temp handling, resume-state handling, aria2 cleanup responsiveness, persistence hardening updates, and FFmpeg mux/concat cancellation completion (DSA-03).
- For future stability work, prefer narrow PRs mapped 1:1 to remaining DSA items to keep rollback and verification simple.


## 6) Closure note

The original download-service stability audit findings are now closed: each runtime-risk DSA item is either completed in a merged PR (including DSA-03 in PR #33) or explicitly tracked as documentation/maintenance context rather than unresolved runtime risk.
