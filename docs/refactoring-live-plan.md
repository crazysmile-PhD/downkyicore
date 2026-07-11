# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-10
Current group: PR 02
Next branch: `refactor/pr-02-host-composition`

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

## Active Next: PR 02 - Project Boundaries And Host Composition

Branch: `refactor/pr-02-host-composition`

- Create `src/DownKyi.Domain`, `src/DownKyi.Application`, `src/DownKyi.Infrastructure`, and `src/DownKyi.Desktop` without moving legacy resources prematurely.
- Add `Microsoft.Extensions.Hosting` and one explicit composition root.
- Define the shared cancellation policy, typed result, and error model.
- Make the real composition root independently testable so MainWindow and key ViewModels can resolve without Prism global `ContainerLocator` state.
- Extend architecture tests to enforce package and namespace restrictions for every new project.
- Preserve existing database, settings, login, portable-mode, and aria2 session paths.
- Document every temporary bridge with its deletion PR; no permanent legacy adapter is allowed.
- Fix the remaining 914 diagnostics across 24 CA rules in separate commits ordered by public API/collection design, then naming/globalization/style; 48 security, async, disposal, exception, lifecycle, null-contract, correctness, API-shape, performance, and allocation rules are already at zero and enforced as errors.
- Preserve the external-protocol hash exception only where a contract test proves it is required; document why it is not a password or trust primitive.
- Promote each fully cleaned rule to `error` in `.editorconfig`; finish with zero unhandled CA warnings and default `CodeAnalysisTreatWarningsAsErrors=true`.
- Make local and CI analyzer settings identical and add required Windows, Linux, and macOS build or smoke coverage.
- Publish a before/after analyzer report and update maintenance, live plan, knowledge graph, and quality workflow in this same PR.

## PR 03-06 - Download Domain And SQLite Store

Branch: `refactor/pr-03-06-download-domain-store`

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

Branch: `refactor/pr-07-15-download-runtime`

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
- Give every DURL segment a stable download key containing `DURL.Order` or an explicit segment index; never use `Bvid.GetHashCode()` or codec `GetHashCode()` as segment identity.
- Sort all DURL inputs by `Order` before queueing or merging.
- For multi-segment DURL output, skip stream copy and rebuild timestamps, keyframes, and MP4 indexes through hardware encoding with CPU `libx264 + aac` fallback.
- Make concat return an explicit success result and validate output with ffprobe: video stream exists, duration is positive and close to summed segments, and middle/tail seeks decode successfully.
- Delete invalid concat output and mark the download failed; callers must not accept `File.Exists(output)` as completion.
- Add regression fixtures proving multi-segment temporary files are unique and merged MP4 output can seek near the middle and tail.

## PR 16-24 - Media Use Cases, ViewModels, And App Lifecycle

Branch: `refactor/pr-16-24-media-ui-lifecycle`

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

Branch: `refactor/pr-25-29-remove-legacy`

- Replace Prism/DryIoc with Microsoft DI, a thin typed router, dialog coordinator, and explicit event streams.
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
