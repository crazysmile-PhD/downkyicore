# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-18
Current group: PR 30-32
Next branch: `refactor/pr-30-32-release-hardening`

This file contains only unfinished work. Completed items are removed in the same PR that finishes them; newly discovered debt is added immediately with an owning PR or phase.

## Branch And Pull Request Policy

- PR 02 uses only `refactor/pr-02-host-composition` and one Pull Request.
- PR 03-06 uses only `refactor/pr-03-06-download-domain-store` and one Pull Request.
- PR 07-15 uses only `refactor/pr-07-15-download-runtime` and one Pull Request.
- PR 16-24 uses only `refactor/pr-16-24-media-ui-lifecycle` and one Pull Request.
- PR 25-29 uses only `refactor/pr-25-29-remove-legacy` and one Pull Request.
- PR 30-32 uses only `refactor/pr-30-32-release-hardening` and one Pull Request.
- A group may contain multiple ordered commits, but it must not be split into smaller public PRs or combined with another numbered range.
- The next group starts only after the previous group has completed its full scope and passed build, tests, data compatibility checks, documentation updates, and `git diff --check`.

## Active Next: PR 30-32 - Profiling, UI, And Release Hardening

Branch: `refactor/pr-30-32-release-hardening`

- Investigate the current 1,488 B/request URL-building allocation only if traces show it is hot.
- Optimize startup history loading, worker limits, caches, and controlled collection parsing only with benchmark or trace evidence.
- Replace the remaining process-global aria RPC configuration with an injected per-runtime client without changing local/custom aria ownership, GID persistence, or resume behavior.
- Complete the logging modernization task derived from `deep-research-report.md` against the current `ApplicationLogProvider`: UTC `YYYY-MM-DD` directories, JSONL streams, 32 MiB rotation, seven-day hard retention, 512 MiB safety cap, active-file protection, startup/hourly/day-change/rotation/pre-export maintenance, and an AI-first redacted export manifest.
- Add deterministic logging retention/rotation/export tests and storage metrics (`capacity_ratio`, age/capacity deletion counts, bytes/events written) before changing the current capacity limit.
- Audit timer/debounce/background-writer ownership across settings and runtime services. Synchronous `Dispose` stops scheduling only; `DisposeAsync` awaits callbacks/pending writes before gates are released. Race tests must use controlled synchronization points, not timing delays.
- Enforce one immutable settings snapshot per HTTP/download/FFmpeg operation while retaining dynamic suppliers only for next-slot global scheduling policy. Add architecture and behavior tests for snapshot consistency and mutable-facade exclusion.
- Audit immutable settings snapshots for mutable nested collections, shallow-copy leaks, staged migration, temporary-file validation, atomic replacement, and interruption safety.
- Apply FluentUI/design tokens only after core ownership and lifecycle are stable; retain virtualization, high-DPI, keyboard, theme, and cross-platform checks.
- Run full Windows/Linux/macOS package smoke tests, binary checksum verification, data migration rehearsal, pause/resume/delete regression, and release artifact validation.

## Execution Rules

- Build and test sequentially; parallel build/test can contend for the same PDB and create a false local failure.
- Every PR must build, test, run, preserve user data, update this plan, update the AI knowledge graph, and pass `git diff --check`.
- PR CI blocks definite failures; benchmarks and noisy system profiling report regressions until stable thresholds exist.
