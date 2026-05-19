# Download stability fixes summary (completed)

This document summarizes the download-stability audit findings that have already been addressed and merged.

Scope: documentation-only summary of completed work from PR #11 through PR #17.

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
| DSA-16 | aria2 completion handler diagnosability cleanup | ✅ Completed | this PR | Added completion-context logs in `AriaDownloadFinish()` without runtime behavior changes. |
| DSA-17 | built-in downloader memory-budget guardrail docs | ✅ Completed | this PR | Added inline comments documenting per-task memory budget and capacity planning formula. |

Related follow-up hardening (not a separate DSA row in the original table):

- PR #17 prevents blocking downloader callbacks while still performing UI-thread marshaling for progress updates.

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

## 3) What was intentionally **not** changed

To keep the above fixes narrow and low-risk, the completed PRs intentionally did **not** redesign broader runtime behavior such as:

- Download service architecture or scheduling model.
- FFmpeg feature set beyond targeted failure/overwrite stability fixes.
- aria2 feature model beyond targeted cancellation cleanup.
- WebClient retry strategy and global HTTP behavior.
- Database/settings schema and persistence model.
- Broad UI interaction model unrelated to progress-thread safety.

## 4) Remaining risks and follow-up recommendations

The following audit items were not part of PR #11–#17 and should remain on the follow-up list:

- DSA-03: FFmpeg mux-phase cancellation support.
- DSA-09: path derivation hardening (`Path.GetDirectoryName` style safety).
- DSA-10: successful concat temp-segment cleanup policy.
- DSA-11: immediate persistence of failed status.
- DSA-12: snapshot/guard mutable per-item collections during persistence.
- DSA-13: unique concat-list temp naming for high concurrency.
- DSA-14: richer failure-context logging in `DownloadFailed` paths.
- DSA-15: remove silent swallow in NFO failure path (log with context).

## 5) Maintainer notes

- Treat PR #11–#17 as a focused “stability batch” that prioritized correctness and safety in: fallback, cancellation cleanup, overwrite protection, and thread-safe progress signaling.
- For future stability work, prefer narrow PRs mapped 1:1 to remaining DSA items to keep rollback and verification simple.
