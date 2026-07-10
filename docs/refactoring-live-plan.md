# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-10
Current branch: `agent/architecture-behavior-baseline`

This file contains only unfinished work. Completed items are removed in the same PR that finishes them; newly discovered debt is added immediately with an owning PR or phase.

## Active Next: PR 02 - Project Boundaries And Host Composition

- Create `src/DownKyi.Domain`, `src/DownKyi.Application`, `src/DownKyi.Infrastructure`, and `src/DownKyi.Desktop` without moving legacy resources prematurely.
- Add `Microsoft.Extensions.Hosting` and one explicit composition root.
- Define the shared cancellation policy, typed result, and error model.
- Make the real composition root independently testable so MainWindow and key ViewModels can resolve without Prism global `ContainerLocator` state.
- Extend architecture tests to enforce package and namespace restrictions for every new project.
- Preserve existing database, settings, login, portable-mode, and aria2 session paths.
- Document every temporary bridge with its deletion PR; no permanent legacy adapter is allowed.

## PR 03-06 - Download Domain And SQLite Store

- Introduce immutable download IDs, media identity, plan, output, progress, failure, phase, and legal state transitions.
- Separate pause, cancel, delete, failure, and retry semantics.
- Build `IDownloadTaskStore`, short pooled connections, real async APIs, migrations, transaction rollback, pre-migration backup, and corrupt-row quarantine.
- Replace the deprecated `SQLitePCLRaw.bundle_e_sqlcipher` only after encrypted legacy database migration and rollback tests prove user data compatibility.
- Stop mapping SQLite rows directly into `DownloadingItem`; current mapping requires Avalonia resources and blocks storage-only round-trip tests.
- Preserve legacy `gid`, partial file maps, downloaded assets, status, progress, and settings snapshots during migration.
- Add keyset history pagination, startup loading of unfinished tasks plus one recent page, and delayed full history.
- Add a bounded write-behind channel that coalesces progress while persisting state transitions immediately.
- Stop converting malformed stored JSON into silent empty collections; report record ID, field, and reason through sanitized diagnostics.

## PR 07-15 - Download, FFmpeg, Aria2, And HTTP Runtime

- Build a bounded download orchestrator, fixed workers, per-task cancellation, global shutdown, staged pipeline, and atomic finalize.
- Unify built-in, local aria2, and custom aria2 behind transfer backends; remove duplicate custom aria2 flow after takeover.
- Preserve resume files on pause and remove media plus `.aria2` / `.download` sidecars on delete.
- Replace static aria2 process state with a process supervisor and bounded shutdown.
- Separate FFmpeg command generation from execution; add capability caching, timeout/cancellation, structured stderr, and bounded transcode concurrency.
- Keep the enforced processing order: stream copy, available hardware encoder, CPU fallback.
- Replace static WebClient with one typed Bilibili client based on `IHttpClientFactory`.
- Make 401/403/schema failures non-retryable, honor `Retry-After` for 429, and keep cancellation non-retryable.
- Replace `BiliApiRequest` catch-and-return-null behavior with typed failures visible to UI and diagnostics.
- Add source-generated JSON contexts and fixed API contract samples for success, missing data, rejected code, HTML, and malformed JSON.
- Make incomplete stream cleanup atomic; a Content-Length failure must not leave a file that can be mistaken for completed media.

## PR 16-24 - Media Use Cases, ViewModels, And App Lifecycle

- Move BV/AV/bangumi/course/collection resolution, parsing, selection, plan building, duplicate policy, and queueing into Application use cases.
- Keep directory-picker cancellation as a normal no-op result with no database write or background task.
- Replace ViewModel `Task.Run` calls with cancellable use cases; 47 active call sites remain, excluding comments.
- Introduce CommunityToolkit.Mvvm and keep ViewModels limited to binding state, commands, navigation, and result projection.
- Fix collection and video-detail item toggle selection, reliable multi-select, and clear-selection beside select-all.
- Fix user-space back navigation, startup URL input being overwritten, and delayed reopen caused by lingering shutdown work.
- Replace conflicting loading booleans with one UI state model.
- Move clipboard, file picker, notifications, dialogs, and navigation behind Desktop interfaces.
- Reduce `App.axaml.cs` to XAML, Host, shell, start, and stop; remove static download collections and service locator calls.
- Replace `OnExitAsync().Wait(15s)` with bounded asynchronous Host shutdown and explicit settings/log flush.

## PR 25-29 - Remove Prism And Legacy Architecture

- Replace Prism/DryIoc with Microsoft DI, a thin typed router, dialog coordinator, and explicit event streams.
- Remove string navigation tags, EventAggregator, Prism commands, region navigation, and global container lookup.
- Delete old download inheritance, `DownloadStorageService`, custom aria2 duplication, SettingsManager singleton, static App collections, console wrapper, dead utilities, old comments, and obsolete packages immediately after new owners pass migration tests.
- Add CI rules that reject new `App.Current`, `Container.Resolve`, `Thread.Sleep`, synchronous async waits, empty catches, `new HttpClient`, mutable static collections, and ViewModel `Task.Run` in the new architecture.

## PR 30-32 - Profiling, UI, And Release Hardening

- Add deterministic startup, working-set, SQLite-write, transfer-throughput, UI-notification, and FFmpeg-concurrency baselines.
- Investigate the current 1,488 B/request URL-building allocation only if traces show it is hot.
- Optimize startup history loading, progress batching, worker limits, caches, and controlled collection parsing with benchmark or trace evidence.
- Apply FluentUI/design tokens only after core ownership and lifecycle are stable; retain virtualization, high-DPI, keyboard, theme, and cross-platform checks.
- Run full Windows/Linux/macOS package smoke tests, binary checksum verification, data migration rehearsal, pause/resume/delete regression, and release artifact validation.

## Execution Rules

- Build and test sequentially; parallel build/test can contend for the same PDB and create a false local failure.
- Every PR must build, test, run, preserve user data, update this plan, update the AI knowledge graph, and pass `git diff --check`.
- PR CI blocks definite failures; benchmarks and noisy system profiling report regressions until stable thresholds exist.
