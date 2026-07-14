# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-14
Current group: PR 16-24
Next branch: `refactor/pr-16-24-media-ui-lifecycle`

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

## Active Next: PR 16-24 - Media Use Cases, ViewModels, And App Lifecycle

Branch: `refactor/pr-16-24-media-ui-lifecycle`

- Move collection parsing, video parsing, selection, plan building, duplicate policy, and queueing into Application use cases; BV/AV/bangumi/course entry resolution, cancellable detail/stream result coordination, directory-cancel/add coordination, and single-source video search projection are complete.
- Move notifications, dialogs, and navigation behind Desktop interfaces; clipboard and file-picker boundaries are complete and their static helpers are deleted.
- Replace `SettingsManager.Instance` with an injected `ISettingsStore`, immutable validated snapshots, schema migration, debounced atomic writes, and explicit async shutdown flush; App/shell, migrated ViewModels/dialogs, media parse/add coordinators, account session, settings pages, and every download backend now share injected owners, while 5 static Core/navigation consumers still reference the singleton directly.
- Replace static `LogManager` usage with injected `Microsoft.Extensions.Logging`, correlation/task/process context, one sensitive-data redactor, bounded recent-event diagnostics, rotation, export, and async shutdown flush; 42 production files still reference the static logger.

## PR 25-29 - Remove Prism And Legacy Architecture

Branch: `refactor/pr-25-29-remove-legacy`

- Replace Prism/DryIoc with Microsoft DI, a thin typed router, dialog coordinator, and explicit event streams.
- Delete `LegacyDesktopComposition`, `MainWindow.AttachLegacyRegion`, and the deferred Prism region attachment after typed navigation owns the shell.
- Remove string navigation tags, EventAggregator, Prism commands, region navigation, and global container lookup.
- Delete old download inheritance, `DownloadStorageService`, custom aria2 duplication, SettingsManager singleton, static App collections, console wrapper, dead utilities, old comments, and obsolete packages immediately after new owners pass migration tests.
- Add CI rules that reject new `App.Current`, `Container.Resolve`, `Thread.Sleep`, synchronous async waits, empty catches, `new HttpClient`, mutable static collections, and ViewModel `Task.Run` in the new architecture.

## PR 30-32 - Profiling, UI, And Release Hardening

Branch: `refactor/pr-30-32-release-hardening`

- Add deterministic cold/warm shell startup time baselines.
- Measure peak working set while restoring unfinished tasks.
- Measure SQLite progress writes per task-minute.
- Measure aggregate transfer throughput with 1, 4, and 8 concurrent tasks.
- Measure UI progress notifications per second.
- Measure FFmpeg CPU/GPU concurrency and peak memory.
- Every system baseline must record runtime, OS, architecture, dataset size, downloader backend, and commit SHA; never compare ad-hoc stopwatch values from different machines.
- Investigate the current 1,488 B/request URL-building allocation only if traces show it is hot.
- Optimize startup history loading, progress batching, worker limits, caches, and controlled collection parsing with benchmark or trace evidence.
- Apply FluentUI/design tokens only after core ownership and lifecycle are stable; retain virtualization, high-DPI, keyboard, theme, and cross-platform checks.
- Run full Windows/Linux/macOS package smoke tests, binary checksum verification, data migration rehearsal, pause/resume/delete regression, and release artifact validation.

## Execution Rules

- Build and test sequentially; parallel build/test can contend for the same PDB and create a false local failure.
- Every PR must build, test, run, preserve user data, update this plan, update the AI knowledge graph, and pass `git diff --check`.
- PR CI blocks definite failures; benchmarks and noisy system profiling report regressions until stable thresholds exist.
