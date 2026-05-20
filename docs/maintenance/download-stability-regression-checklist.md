# Download stability regression checklist

## 1) Purpose

This checklist is used to manually verify that the completed download stability batch (PR #11 through PR #35) continues to behave correctly after future changes.

Coverage focus:

- backup URL fallback
- cancellation cleanup
- FFmpeg mux/concat failure recovery
- output overwrite safety
- path derivation guards
- concat temp cleanup
- built-in resume handling
- Aria2 cleanup responsiveness
- failed-state persistence
- persistence collection snapshots
- FFmpeg mux/concat cancellation

## 2) Test matrix

| Area | Scenario | Expected result | Related PR |
|---|---|---|---|
| Built-in downloader | Built-in downloader normal success. | Download completes, final file exists, and task ends in success state. | #11, #14, #27 |
| Built-in downloader | Built-in downloader first URL fails, backup URL succeeds. | Fallback URL is attempted and download completes without terminal failure on first URL error. | #11 |
| Built-in downloader | Built-in downloader cancellation during active transfer. | Cancellation stops transfer quickly, performs cleanup, and does not leave task running. | #14 |
| Built-in downloader | Built-in downloader resume after pause. | Resumed task continues and reaches correct completion/cancellation state. | #27 |
| Aria2 | Aria2 normal success. | Download completes and local state is consistent with finished result. | #15, #28 |
| Aria2 | Aria2 cancellation during active transfer. | Cancellation is responsive and task is cleaned up without stale running state. | #15, #28 |
| Aria2 | Aria2 cleanup after failed task. | Failed/cancelled task cleanup path clears remote/local tracking state without blocking UI flow. | #28 |
| FFmpeg mux | FFmpeg mux success with audio + video. | Mux output succeeds and final merged output is generated correctly. | #13, #33 |
| FFmpeg mux | FFmpeg mux failure preserves temp audio/video inputs. | Temp input streams are preserved for recovery/retry analysis when mux fails. | #12 |
| FFmpeg mux | FFmpeg mux cancellation does not become `DownloadFailed`. | Cancelled mux is classified as cancelled outcome, not failure. | #33 |
| FFmpeg concat | FFmpeg concat success removes only owned temp segments. | Cleanup removes only safe owned concat temp artifacts after successful concat. | #26 |
| FFmpeg concat | FFmpeg concat cancellation does not run success cleanup. | Cancelled concat does not execute success-only cleanup branch. | #33 |
| Output safety | Existing final output path triggers safe output naming. | Existing target path is handled safely (no unsafe overwrite/data loss behavior). | #13 |
| Path guards | Invalid or null-like download path fails safely. | Invalid path input is rejected/handled safely without undefined behavior. | #24 |
| Persistence | `DownloadFailed` persists failed status immediately. | Failed state is written immediately and visible after process restart. | #30 |
| Persistence | `DownloadFiles` / `DownloadedFiles` persistence uses snapshots. | Persistence serializes stable snapshots and avoids collection mutation race issues. | #31 |
| Diagnostics | NFO generation failure logs but remains non-fatal. | NFO errors are logged and do not crash/abort unrelated download flow. | #20 |
| Diagnostics | Failure-context logging does not include cookies/tokens. | Failure-context logs remain useful while excluding sensitive cookies/tokens/auth secrets. | #19 |

## 3) Manual verification commands / observations

Use these checks during manual regression runs (they are observational checks, not strict automated tests):

- Verify final output exists and has non-zero size for success paths.
- Verify temp audio/video inputs are preserved on mux failure/cancellation cases that require recovery analysis.
- Verify cancelled task is not marked `DownloadFailed`.
- Verify failed task remains failed after restart.
- Verify logs include actionable failure context (task/stage/error classification).
- Verify logs do **not** include cookies, tokens, or auth headers.

## 4) Non-goals

This checklist does **not** validate:

- Bilibili API availability
- external network speed
- codec compatibility across all devices
- UI redesign
- database migration behavior
- package upgrades

## 5) Future automation candidates

Potential automated test candidates for future batches:

- pure helper tests for path derivation
- FFmpeg result classification tests
- storage snapshot helper tests
- failure persistence tests
- log redaction tests
