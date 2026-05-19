# DSA-03 FFmpeg mux-phase cancellation plan (closed)

## Status

✅ Completed in PR #33.

This document is retained for historical traceability of the original implementation plan. The planned runtime work has been implemented and merged; DSA-03 should now be treated as closed in active audit tracking.

## Historical plan (archived)

## Problem

The download flow can enter FFmpeg mux/merge/concat phases after media download is complete. If the user cancels during mux, the current process may not propagate cancellation into the FFmpeg process lifecycle.

## Risk

- FFmpeg may continue running after user cancellation.
- Temp files may be deleted or preserved inconsistently.
- Final output may be partially written.
- Failed/cancelled status classification may become ambiguous.
- Killing FFmpeg incorrectly may corrupt recoverable temp inputs.

## Required future behavior

A future runtime PR should ensure:

1. User cancellation during mux attempts to stop the active FFmpeg process.
2. Cancelled mux should not be reported as successful completion.
3. Recoverable audio/video temp inputs should be preserved on cancellation.
4. Partial final output should not be treated as valid final output.
5. Cancellation should remain distinct from ordinary failure.
6. Existing mux success behavior should remain unchanged.
7. Existing retry behavior should remain unchanged unless explicitly justified.

## Implementation constraints for future PR

The future runtime PR must not combine DSA-03 with:

- package updates
- path rewrite
- retry redesign
- Aria2 changes
- built-in downloader changes
- WebClient changes
- UI redesign
- database schema changes
- nullable warning cleanup

## Suggested future implementation strategy

Recommend a narrow follow-up PR:

```text
fix: support cancellation during ffmpeg mux phase
```

Suggested strategy for that runtime-only PR:

1. Introduce a small cancellation-aware mux execution wrapper in the FFmpeg integration boundary (not in downloader implementations).
2. Thread the existing download cancellation token from `DownloadService.BaseMixedFlow()` / `ConcatVideos()` call sites into mux execution only.
3. On cancellation request during mux, attempt graceful process termination first, then forceful kill if required by timeout policy.
4. Return an explicit mux result classification (`Success`, `Cancelled`, `Failed`) so callers can map states without ambiguity.
5. Treat `Cancelled` as a non-success terminal path and avoid success-side cleanup/status transitions.
6. Preserve recoverable temp audio/video inputs on `Cancelled` and on non-success mux outcomes where retry is meaningful.
7. Mark any partial final output as invalid (delete or quarantine consistently) before exiting cancellation flow.
8. Add focused logging for mux start/stop/result classification and cancellation path decisions (without broad logging refactors).
9. Verify no behavioral drift in existing successful mux and existing retry loops.

## Validation checklist for future runtime PR

- Cancel during long mux causes FFmpeg process to stop promptly.
- UI/state ends as cancelled, not completed.
- Temp inputs remain available for manual/automatic retry paths.
- Partial final output is not treated as finished output.
- Success path for non-cancelled mux remains unchanged.
- No unrelated subsystem changes are included in the PR scope.
