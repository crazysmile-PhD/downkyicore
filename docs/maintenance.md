# Maintenance Guide

This document records the project maintenance routine for dependencies, external binaries, release validation, and regression checks.

## Dependency Updates

1. Update managed package versions only in `Directory.Packages.props`.
2. Run `dotnet restore ./DownKyi.sln`.
3. Run `dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental -p:TreatWarningsAsErrors=true -p:CodeAnalysisTreatWarningsAsErrors=true -p:EnableNETAnalyzers=true -p:AnalysisMode=All -p:EnforceCodeStyleInBuild=true`.
4. Run `pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild`.
5. Run `dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive`.
6. Run `dotnet package list --project ./DownKyi.sln --deprecated` and review the report.

Avoid mixing package updates with large refactors unless the refactor is required by the dependency change.

## CI Policy

Assembly/process lifecycle is a separate quality dimension from test
assertions. Run `script/audit-lifecycle-ownership.ps1` after changing any
thread, Dispatcher, timer, Host, global event, fixture or external-process
owner. Run `script/test-assembly-lifecycle.ps1` before release; its PR, main and
rehearsal profiles execute 3, 50 and 100 iterations per test assembly. The
contract, diagnostics and report schema are documented in
`docs/testing/assembly-lifecycle-stability.md`.

Formal local verification runs the ownership audit followed by five iterations
per test assembly with `-ValidateForensics`. The release workflow uses the
`Rehearsal` profile and writes the full 100-iteration report below
`artifacts/assembly-lifecycle/release`; neither step may be replaced by a
successful one-off rerun.

Lifecycle report schema 2 uses the child OS `Process.ExitTime` for post-fixture
exit, captures marker-aware execution at the unchanged slow threshold, records
diagnostic collection wall time, and fails a slow phase whose evidence is
missing. Schema 1 exit values include collector overhead and are historical
only; do not compare them directly with schema 2.

The slow classification threshold remains five seconds. Evidence collection
is armed 1,000 ms before that boundary so hosted-runner scheduling cannot
observe a borderline process only after it has exited. Reports expose both the capture
lead and whether a phase was sampled before the classification boundary;
`capture-failed` and `process-exited-before-capture` remain fail-closed for
phases whose final duration reaches the threshold.
The `-ValidateForensics` held-child self-test must also set
`forensicsSelfTestCaptureLeadValidated=true`, proving this arm point executed
rather than merely existing in source.

Formal Windows PR, Main, Rehearsal and Flaky lifecycle profiles require
`-ValidateForensics`. Their schema 2 report must show a detailed
`markerReaderSelfTest` with `executed`, `passed`, `contentionObserved`,
`contentionCount > 0`, `recoveredAfterLockRelease` and
`markerParsedAfterRecovery` all true, plus `errorType == null`. Its contract
mutation checks must also pass. Missing, null, unknown or non-contending proofs
fail closed; the top-level
`markerReaderSelfTestPassed` value is only a summary.
The marker self-test phase, summary and final formal contract must consume the
single `markerReaderSelfTestComplete` result; do not re-expand the predicate.
General lifecycle failures belong in phase `failureType` / `errorType`, while
`slowEvidenceErrorType` is only for slow-evidence collection.

Only Windows sharing/lock error codes count as marker contention.
`UnauthorizedAccessException` and other I/O errors remain separately visible
as `markerReadErrorCount` and `markerReadErrorType`.

Residual-child failures are independently fail-closed. Every failed phase must
preserve a sanitized `residualChildren` identity list plus a
`residual-children.json` manifest; live managed children also receive thread,
tree and managed-stack evidence. `-ValidateForensics` must prove this path by
creating a controlled residual child, observing its identity, writing evidence,
classifying the phase as `ResidualChildProcess` and cleaning the synthetic tree
by PID plus creation time. It must also prove path, URL, cookie and secret
redaction. `residualChildSelfTestPassed` is only the summary;
the detailed `residualChildSelfTest` fields are the contract.

PR #116 merged the final lifecycle proof consistency fix into `main` at
`6a61247`. Strict PR CI `30450175286` and CodeQL `30450175415` passed. Its
Main profile report contains 2,102 phase results across seven assemblies and
50 iterations, with zero failures, zero missing slow evidence and zero marker
read errors. Teardown max is 7 ms and OS process-exit max is 187 ms; all 14
slow execution phases retained evidence. This report validates the corrected
lifecycle owner and gate, but the final versioned release commit must still
pass its own Main profile and the 100-iteration Rehearsal profile.

The first v1.1.1 Rehearsal run `30455540672` correctly blocked packaging after
`DownKyi.Tests` assembly-info iteration 78 observed one residual child. The
historical report retained only `residualChildCount=1`, so that runner's exact
child identity cannot be recovered after the hosted VM was destroyed. The
phase did not execute tests, Host, Dispatcher or application services; its
stdout was valid xUnit metadata, stderr was empty and exit code was zero.
Five hundred local repetitions of the same
`dotnet DownKyi.Tests.dll -assemblyInfo` command observed no residual child,
excluding a deterministic
application owner but not the low-probability runner race. The corrected gate
therefore preserves identity and evidence on every future observation and
dynamically self-tests the full residual-child failure path. A new Main profile
and complete Rehearsal remain mandatory; the failed run is not replaced by a
blind rerun.

After PR #118 merged at `ad5ac64`, Main run `30461640781` proved that the
original 100 ms capture lead was still insufficient under hosted-runner load.
`DownKyi.Tests` execution iteration 24 exited successfully at 5007.386 ms but
the monitor never entered the capture branch while the process was alive.
The report failed closed with `SlowEvidenceMissing`; residual-child and
marker-reader self-tests passed and no real residual process was observed.
The five-second classification threshold remains unchanged. The capture arm is
now one second early, and architecture tests prevent reducing it back to the
demonstrably insufficient 100 ms window. The held-child self-test uses a
1.25-second synthetic threshold so it dynamically proves capture starts after
0.25 seconds and before classification, without relying on a zero-clamped arm.

Pull requests are guarded by `.github/workflows/quality.yml`:

- format check with `dotnet format --verify-no-changes --verbosity diagnostic`
- Windows, Linux, and macOS Release builds
- compiler and all `AnalysisMode=All` CA diagnostics treated as errors
- unit tests with uploaded TRX reports
- assembly load/info/discovery/execution/teardown/exit stability on Windows
- transitive vulnerable package audit
- deprecated package report

Release tags are additionally checked by
`script/validate-release-version.ps1`. `version.txt` must contain one stable
`major.minor.patch` value, and a tag workflow may proceed only when
`GITHUB_REF` equals `refs/tags/v<version>`. The v1.1.0 tag is immutable and its
withdrawn draft must not be republished; the corrective release is v1.1.1.

Gate 10 v1.1.0 integration candidate `355ef7cb` passed the local strict
`AnalysisMode=All` Release build with zero warnings/errors, all 714 tests,
format verification, module-boundary audit, vulnerable/deprecated dependency
audits and `git diff --check`. Its explicitly authorized authenticated
read-only Bilibili audit was repeated at committed candidate `8aa4382`; the
`/nav` login gate and all 14 allowlisted contracts passed with zero drift. The
resulting machine-readable artifact contains only sanitized contract metadata;
Gitleaks 8.30.1 inspected all 986 tracked and non-ignored untracked candidate
files and reported zero findings. Final PR quality, CodeQL and the
cross-platform package rehearsal remain release requirements and are recorded
in `docs/refactoring-live-plan.md`.

PR #112 first-head quality run `30426137294`, protobuf run `30426137279`
and CodeQL run `30426137276` passed. Manual release rehearsal `30426554087`
then correctly blocked publication: BtbN had removed the pinned 2026-07-08
autobuild, and `DownKyi.Core` derived the SDK `RuntimeIdentifier` from the
runner host, causing cross-RID restore races on Windows x86 and macOS x64.
The repaired candidate pins the existing 2026-07-28 FFmpeg release assets and
their upstream SHA-256 digests, makes all four asset scripts independent of the
caller's working directory, and makes the Bash aria2 script consume the shared
manifest. `DownKyiAssetRuntimeIdentifier` now selects package content without
  setting the SDK `RuntimeIdentifier`; package jobs explicitly restore their
  target RID. Local strict build has zero warnings/errors, all 718 tests pass,
and an actual Windows x86 self-contained publish passes the common package
validator with non-empty DownKyi, aria2, FFmpeg and ffprobe files. The complete
remote rehearsal `30428876552` proved that the expired-asset and SDK RID fixes
worked, but exposed a narrower cross-target content issue on macOS x64,
Windows x86 and Linux arm64: MSBuild did not automatically propagate the
target asset RID through project references. The executable is now the only
owner of target or local-host fallback selection and directly includes the
matching external assets in output and publish; the custom property never
crosses a project-reference boundary, and Core is a runtime-neutral library.
A fresh Windows x86 self-contained publish passes the common package
validator, and its aria2, FFmpeg and ffprobe hashes exactly match the x86
source assets. Passing a custom RID through project-reference metadata was
explicitly rejected because it creates multiple project instances that race on
the same `obj/bin` paths during a solution build. The final ownership model
passes the strict Release build with zero warnings/errors, all 719 tests,
format, module-boundary, dependency, secret and diff gates.

PR #112 quality run `30430722500`, protobuf run `30430722468` and CodeQL run
`30430722421` pass on `1968c9d`. Windows, Linux and macOS each retain seven
distinct TRX files with 718 executed/passed tests and no failures; the only
non-executed result is the FFmpeg-runtime seek integration that passes locally.
Code and test annotations are zero. The single Analyze C# warning is GitHub's
platform notice that a PR with more than 300 changed files cannot expose its
complete diff to CodeQL, not an analyzer finding.

Manual rehearsal `30431043860` passes all three release gates and all nine
package jobs; manual dispatch correctly skips GitHub Release publication.
Every downloaded package SHA-256 sidecar matches, all nine publish manifests
agree on version/RID and contain 54 valid required-file entries, and manifests
for repeated package formats of the same RID are identical. Direct content
inspection covers both Windows zips, both debs, the rpm, both AppImages and
both DMGs. Every package contains DownKyi, aria2, FFmpeg, ffprobe and Fluent
runtime content, with no Config, Logs, Cache, Storage, Cookie, SQLite/database
or user-data path. Remote Windows binary hashes also match the checked-in
runtime catalog.

The repository always uses the supported `AnalysisMode=All` value. The pre-fix baseline is 1,654 unique diagnostics across 71 CA rules; see `docs/analyzer-baseline.md` and `docs/analyzer-baseline.csv`. `CodeAnalysisTreatWarningsAsErrors=true` is the repository default. Every cleaned rule is also pinned to `error` in `.editorconfig`, preventing a future SDK severity change from reopening the baseline. The before/after inventory and retained exceptions are recorded in `docs/analyzer-cleanup-report.md`.

Current analyzer result: zero unhandled CA diagnostics. All 77 cleaned rules are enforced as errors, and the full solution defaults to `CodeAnalysisTreatWarningsAsErrors=true`. Public fields were converted only after checking JSON names, Avalonia bindings, inheritance, and download lifecycle ownership. Indexable collections now use direct indexing without changing empty-list behavior, and property/JSON names use compile-time `nameof` where the wire value is identical. Executable-only application, UI, service, model, and helper types are internal; clean Release compilation verifies Avalonia XAML can still construct its backing types. Public NFO XML contracts remain in Core because `XmlSerializer` requires public root/member types; namespace, XML names, collections, and serialized shape are covered by round-trip tests. Raw Bilibili/aria2 addresses retain string storage and exact JSON keys; login QR and redirect consumers validate absolute `Uri` values at the boundary, while protocol-relative media addresses remain supported. Benchmark cases live in the public, non-sealed `DownKyi.BenchmarkCases` assembly because BenchmarkDotNet generates derived types through reflection; the runner remains internal and validation confirms a result row exists. Async commands use the protected can-execute raiser, dialogs complete typed results, and user-space tab payloads travel through `AppNavigationRequest.Parameter`. Diagnostic hashes use uppercase SHA-256 fragments, NFO booleans use lowercase literals, and FFmpeg cleanup failures use the shared injected logger without duplicate terminal output. JSON/XML/SQLite contracts, enum numeric values, ordinal protocol comparisons, and DURL `Order` identity are all guarded by tests.

## Download Persistence Policy

- `src/DownKyi.Domain/Downloads` owns immutable task identity, lifecycle, progress, transfer, output, failure, and completion state.
- `IDownloadTaskApplicationService` / `DownloadTaskApplicationService` is the only normal runtime command/query owner. It loads by `DownloadTaskId`, invokes a Domain transition, persists with the current optimistic version, and publishes the committed snapshot only after a successful store operation.
- `IDownloadTaskStore` is the only durable download contract. Infrastructure implementations must use async APIs and honor cancellation.
- `SqliteDownloadTaskStore` is the sole owner of download SQL and storage JSON. `DownloadTaskProjectionStore` maps immutable stored tasks to existing `DownloadingItem` / `DownloadedItem` UI projections without owning SQL.
- Runtime code must not rebuild a Domain task from a mutable UI projection. `DownloadTask.Restore` is limited to `SqliteDownloadTaskStore` materialization and `LegacyDownloadTaskMapper` migration; architecture tests enforce that allowlist.
- Use short pooled connections, WAL, optimistic task versions, and transactions. Never restore a process-wide SQLite connection, global database lock, `Task.Run` database wrapper, or offset-based history scan.
- Every schema migration must create a SQLite backup before DDL, execute in one transaction, update `user_version` only on success, and have a rollback test.
- Malformed rows are quarantined individually. Diagnostics may include source table, record ID, field, and a fixed reason; never include raw JSON, full paths, cookies, or URLs.
- Startup loads every unfinished task and the newest 100 history records after shell creation. Remaining history uses keyset pages outside the first-screen path.
- State transitions persist immediately and UI projection follows the committed event. High-rate live progress may be projected without a durable write for every sample; accepted persistence uses bounded/coalesced writes and shutdown recovery preserves the last durable resume state.
- The SQLite native bundle is `SQLite3MC.PCLRaw.bundle`. Any update must pass `LegacySqlCipherCompatibilityTests` against the committed SQLCipher v4 fixture before merge.

Gate 4 local result: strict `AnalysisMode=All` Release build completed with zero warnings, all 552 solution tests passed, format changed 0/750 files, `git diff --check` and the module-boundary audit passed, and NuGet reported no vulnerable or deprecated packages. Tests cover legal state transitions, optimistic conflicts, one-way projection, legacy NRBF/SQLite materialization, pause confirmation, retryable deletion, shutdown recovery, GID/partial-file/completed-key persistence, progress and output-size reopen, and the architecture allowlist for `DownloadTask.Restore`.

PR 03-06 result: legacy GID, partial-file maps, completed asset keys, paused state, progress, task identity, and history survive reopen. Completion moves from active state to history in one transaction. The removed deprecated SQLCipher provider was replaced only after the current cross-platform provider opened the old encrypted fixture and rejected a wrong password. Release build, all tests, isolated App startup/close, Linux x64/arm64 and macOS x64/arm64 cross-RID builds, deprecated-package audit, and vulnerable-package audit passed locally.

## Download And Media Runtime Policy

- Queue consumption uses a bounded Channel and fixed workers. Do not restore per-item task spawning or synchronous persistence callbacks.
- New, resumed, and startup-restored work enters through `DownloadTaskQueueGateway` as `DownloadTaskId`. The unbounded admission channel isolates UI/startup callers from worker-channel capacity; only the internal dispatcher waits for bounded worker capacity. Runtime scheduling must never poll `ObservableCollection`.
- Built-in and aria2 transfers share key, resume, integrity, and persistence behavior. Custom aria2 is a backend selection, not a copied workflow.
- `DownloadArtifactWriter` owns cover, subtitle, danmaku, and NFO output. `DownloadTaskStateWriter` adapts runtime calls to typed Application commands; it cannot accept `DownloadingItem` or persist reconstructed UI state.
- Pause first persists `Pausing`; built-in/aria2 backends preserve partial files and return a paused outcome; the worker confirms `Paused` only after transfer teardown. Process shutdown returns active `Downloading`/`Pausing` tasks to `Queued` without dropping GID, partial-file maps, completed keys, progress, output size, or optimistic version.
- Explicit task deletion persists `Canceled`, stops the physical backend, removes generated media and `.aria2` / `.download` sidecars, then deletes the row. A cleanup failure leaves a retryable canceled record rather than pretending deletion succeeded.
- Multi-segment DURL identity includes `DURL.Order`, input is sorted by that order, and concat never starts with stream copy.
- FFmpeg operations use `FfmpegProcessRunner`, bounded concurrency, cancellation, timeout, captured stderr, and process-tree cleanup. Hardware encoding is attempted when available, with CPU fallback kept for success rate.
- A multi-segment output is complete only after ffprobe confirms a video stream, expected duration, and successful middle/tail seek decoding. Invalid partial output is deleted.
- Bilibili requests use injected `IBilibiliApiClient` and `IBuvidProvider` ports backed by the Infrastructure `IHttpClientFactory` transport. Endpoint adapters are async; static client state, global configuration and synchronous HTTP compatibility paths are prohibited.
- HTTP 401/403 and API schema rejection are non-retryable, 429 honors bounded `Retry-After`, cancellation is never retried, and empty/HTML/malformed responses fail visibly.

Gate 5 result: strict `AnalysisMode=All` Release build completed with zero warnings, all 565 solution tests passed, format verification and `git diff --check` passed, the module-boundary audit completed, NuGet reported no vulnerable or deprecated packages, and the candidate-file Gitleaks scan reported zero findings. Tests cover direct admission, pre-start buffering, deduplication, 1/4/8 worker execution, per-task cancellation, startup recovery, shutdown recovery, and architecture-test isolation from ignored local tooling trees. PR #88 also passed the Windows/Linux/macOS quality matrix and CodeQL.

Loopback cancellation tests must synchronize on the server receiving the first request before canceling. Fixed timeouts are not an acceptable substitute because a loaded Windows runner can cancel before socket acceptance and produce a false zero-request failure.

Gate 6 stage-extraction result: `DownloadPipeline` is 125 physical lines and only sequences six typed stages; every extracted stage remains below 500 lines. Strict `AnalysisMode=All` Release build completed with zero warnings, all 574 solution tests passed, format verification and `git diff --check` passed, the module-boundary audit completed, NuGet reported no vulnerable or deprecated packages, and Gitleaks reported zero findings across 894 candidate files. Deterministic tests cover stage order, first-failure short circuit, operation-token forwarding, DURL/DASH format detection, stable DURL transfer keys, retained expected byte length, mux output selection, injected completion time, requested-output validation, path handling, and image URI handling. PR #89 passed two Windows/Linux/macOS quality and CodeQL rounds and was merged into the stacked release-hardening base as `e288913f`. It did not change JSON settings, SQLite rows, task IDs, GIDs, partial-file maps, completed transfer keys, or resume semantics.

Gate 6 retry-policy local result: `DownloadTransferCoordinator` now owns one five-attempt budget across primary URLs, backups and one refreshed playback response. Built-in Downloader retries are disabled with `MaxTryAgainOnFailure=0`; each aria2 task receives `max-tries=1`, `retry-wait=0`, `always-resume=false` and `max-resume-failure-tries=0`, and each aria RPC client call makes one physical request. Backends return typed failures, cancellation propagates, disk failures stop immediately, 403 can refresh addresses once, invalid media rotates after corrupt artifacts are removed, and retryable failures retain partial/resume files. A rejected resume state removes only the affected output and sidecars before one same-address retry. aria2 error codes are classified by their documented failure semantics; an RPC error or empty envelope terminates that poll immediately, RPC-layer failure retains the latest GID, and terminal task failure or explicit task-not-found clears stale identity. Strict `AnalysisMode=All` Release build completed with zero warnings and all 615 solution tests passed. Format verification, `git diff --check`, the module-boundary audit, vulnerable/deprecated package audits and the 900-candidate Gitleaks scan are green. PR #90's implementation head passed Windows/Linux/macOS quality run `30187122186` and CodeQL run `30187122221`; its final documentation head must pass the same remote gates before integration.

CodeQL uses explicit `manual` build mode so generated and build-resolved C# remains in the high-accuracy full database. `CODEQL_OVERLAY_DATABASE_MODE=none` disables the incompatible incremental overlay optimization; do not switch to buildless `none` mode only to remove an annotation, because that can omit generated code.

Gate 8 result: `DownKyi` is a one-file bootstrap, `DownKyi.Desktop` owns App/UI/presentation/runtime, and Core contains no Avalonia, QRCoder, or XAML. Projection models live under `DownKyi.Presentation`; `DownloadListState` exposes standard read-only wrappers over owner-only mutable collections, while service-interface references to `DownKyi.ViewModels` and production references to the deleted custom collection are both zero. Strict `AnalysisMode=All` Release build completed with zero warnings and all 610 solution tests passed. Format verification changed 0/791 files, `git diff --check` passed, the module audit reported zero Core UI dependencies and zero presentation-bound contracts, NuGet reported no vulnerable or deprecated packages, and Gitleaks reported zero findings across 916 candidate files. Moving `ColorBrush.axaml` required updating its existing exact-path/exact-line false-positive allowlist; no directory-wide or secret-pattern exclusion was added. PR #92 passed Windows/Linux/macOS quality run `30191251004`, protobuf run `30191250997`, and CodeQL run `30191250992`, then merged into the stacked release-hardening base as `f8e78c9a`. The merge ref had zero open CodeQL alerts; GitHub's only annotation was its 300-file PR diff limit for this required 396-file ownership move.

Gate 9 logging local result: Application owns diagnostic contracts and Infrastructure owns the private NLog 6.1.4 provider, bounded sink, redactor, recent buffer, retention and file-backed exporter; Core contains no logging implementation. A 400-record concurrent flush test first exposed whole-configuration loss, and the complete solution later exposed a final-entry disposal race that focused tests had missed. The final sink keeps its async wrapper alive, defers concurrent producers through a bounded barrier, and resets only `FileTarget` handles. Five focused logging rounds, ten complete Infrastructure rounds and all 616 solution tests passed. Strict `AnalysisMode=All` Release build had zero warnings/errors; format changed 0/800 files; module boundaries and vulnerable/deprecated package audits passed; Gitleaks found zero findings across 935 candidate files. The final same-machine 10,000-event benchmark wrote every event with zero drops; its slower flush, larger allocation and one slower producer run remain explicit non-gating evidence.

Gate 9 aria RPC client local result: source history classifies `AriaClient` as hand-maintained DownKyi protocol code first added by commit `587fcfb`, not generated output or a separately synchronized package. The 1,119-line owner is now six responsibility partials between 65 and 333 lines, and the oversized production-file baseline is empty. A deterministic test invokes all 36 public RPC methods and fixes their JSON-RPC method name, ID and token-placement contracts. It exposed and fixed a pre-existing `ChangeUriAsync` defect that sent `aria2.changePosition` instead of `aria2.changeUri`. Strict `AnalysisMode=All` Release build completed with zero warnings/errors; all 713 tests passed across seven test projects; format changed 0/849 files; module boundaries, vulnerable/deprecated package audits and `git diff --check` passed; Gitleaks found zero findings across 985 candidate files. PR #111 implementation head passed Windows/Linux/macOS quality run `30424513258` and CodeQL run `30424513284`; all seven checks had zero annotations and each platform artifact contained seven distinct assembly-named TRX files. The final documentation head must pass the same gates before integration.

PR 07-15 result: Release build completed with zero warnings, 161 tests passed including real FFmpeg/ffprobe seek validation and Host smoke without Prism global container state, format verification passed, and both vulnerable and deprecated package audits were clean. Cross-RID Release builds passed for Windows x86, Linux x64/arm64, and macOS x64/arm64. An isolated Windows process smoke created the main window, accepted close, and exited with code 0 without reading or writing real user data. Native Linux/macOS execution remains owned by their CI runners.

## Settings Persistence Policy

- `ISettingsStore.Current` is the validated immutable read contract. Production consumers must use `Current` and typed `Update` calls; the non-singleton `SettingsManager` is an internal persistence implementation constructed only by `SettingsStore`.
- Correlated settings changes use one `Update` call. This prevents another consumer from observing half of a proxy, content-selection, or related multi-field update.
- `SchemaVersion` advances only through `SettingsSchemaMigrator`, one explicit version at a time. A migration must preserve existing JSON property names unless a separately tested compatibility migration is approved.
- Malformed settings are moved to a unique `.invalid-*` backup before safe defaults are persisted. Do not log the payload or its personal path.
- A file with a schema newer than the running application is read only for safe fallback and must remain byte-for-byte unchanged.
- Persistence is debounced, serialized through one async gate, written to a UTF-8 temporary file, flushed, and atomically replaced. Do not restore synchronous whole-file writes or wrap them in `Task.Run`.
- Debounce uses one tracked cancellation-aware Task, not a `Timer` callback. A replacement update cancels the previous delay; final async disposal awaits the last accepted write before releasing the gate.
- The temporary JSON file must parse as one complete object before atomic replacement. Invalid or interrupted temporary output cannot replace the last valid settings file.
- Each HTTP, download-planning, transfer, artifact, diagnostic, and FFmpeg operation captures one immutable snapshot. Dynamic setting suppliers are reserved for policy selection for the next queued worker slot, never for changing an operation already in progress.
- Nested settings collections are immutable arrays. Publishing a later update cannot mutate an earlier operation snapshot.
- Application shutdown must await `FlushAsync`. Owners that require pending changes to persist during disposal use `DisposeAsync`; synchronous `Dispose` only stops scheduled work.
- The historical DES reader remains read-only. It may decrypt supported old settings once, but no code may use DES to write new data.

Settings changes must pass `SettingsStoreTests`, `SettingsArchitectureTests`, the Host smoke test, and the full Release build with `AnalysisMode=All`.

## Logging Policy

- New code receives `ILogger<T>` from composition and must not call static `LogManager` or write diagnostics directly to Console.
- Application-facing records, metrics and `IApplicationLogService` live in `DownKyi.Application.Diagnostics`. The provider, redactor, NLog sink, recent buffer, retention and exporter live in `DownKyi.Infrastructure.Logging`; Core and Desktop cannot own a logging implementation.
- `ApplicationLogProvider` is the single MEL adapter and redaction boundary. It delegates to separate bounded recent-buffer, private NLog sink, retention and file-backed exporter owners. Do not create another log queue, file writer or export sanitizer.
- Only NLog core is allowed. The sink owns a private `LogFactory`; global `LogManager` and `NLog.Extensions.Logging` are prohibited.
- Keep per-record writes in the private `ReopenableFileTarget` batch override. NLog flushes the complete async queue as one `FileTarget` batch regardless of wrapper batch size, so the override is what guarantees a size-roll check between JSONL records while producers remain asynchronous.
- Logging scopes carry correlation, download-task, or child-process context; messages must not contain raw cookies, sensitive query values, account IDs, email addresses, or full personal paths.
- The writer queue and recent-event buffer stay bounded. A full queue may drop an entry and increments the diagnostic drop counter; logging must never block a download or UI thread.
- Redaction completes before a record reaches NLog or the recent buffer. JSON serialization runs on NLog's async target through the thread-agnostic project layout.
- Application shutdown must await `FlushAsync` and `DisposeAsync`. Explicit flush uses the bounded deferred-write barrier before resetting only the `FileTarget` handles, so the Log page can open the file immediately on Windows without losing concurrent records or rebuilding the async logger configuration.
- Writer initialization or persistence failures must reach the caller of `FlushAsync`; `DisposeAsync` completes cleanup and then rethrows the first persistence failure. Do not silently report a successful flush or shutdown.
- Files use UTC `yyyy-MM-dd` directories and JSONL records. Rotation defaults to 32 MiB, hard retention to seven days, and the storage safety cap to 512 MiB; maintenance protects the active file and runs at startup, hourly, day change, rotation, and before export.
- Diagnostic export reads persisted files after flush, re-redacts defensively, skips malformed records with a metric, and writes a redacted JSON manifest plus bounded events. Metrics expose capacity ratio, age/capacity deletion counts, bytes/events written and malformed records; capacity changes require deterministic retention evidence first.

Logging changes must pass Infrastructure `ApplicationLogProviderTests`, including concurrent producer/flush stress, the Host smoke test, and the full Release build with `AnalysisMode=All`.

## Desktop Theme Policy

- Desktop uses `Avalonia.Themes.Fluent` and the Fluent DataGrid theme. Do not reintroduce the Simple theme or load two control themes in the same App.
- Shared typography, spacing, radius, elevation, control-height, and progress-thickness values live in `DownKyi/Themes/DesignTokens.axaml`.
- `ThemeDefault.axaml` retains both `Default` and `Dark` color dictionaries. Theme work must preserve keyboard focus, high-DPI sizing, and existing localized resources.
- Download, history, and favorites lists must retain `VirtualizingStackPanel`; styling changes cannot trade large-list responsiveness for visual uniformity.
- Theme changes require `UiThemeArchitectureTests`, real Host XAML smoke, and an isolated packaged-App startup on Windows. Native Linux/macOS construction remains enforced by the CI matrix.

PR 30-32 validation: strict `AnalysisMode=All` Release build completed with zero warnings, all 468 tests passed, format verification changed 0/719 files, and vulnerable/deprecated package audits were empty. `actionlint` accepted the quality, system-baseline, and release workflows. The isolated Windows x64 quick system suite produced all shell, restore, SQLite, transfer, UI, CPU-FFmpeg, and NVENC scenarios with complete environment metadata. Real Windows x64 and x86 publishes passed version/binary/theme validation, created the main window from isolated data, and terminated their aria2 child after forced parent exit. GitHub Actions run `29636597704` then passed all native Windows/Linux/macOS release gates plus Windows x86/x64 ZIP, Linux x64/arm64 AppImage/deb/rpm, and macOS x64/arm64 DMG package jobs; manual execution correctly skipped release publication.

## Host Composition Policy

- `src/DownKyi.Domain` is framework-free and owns typed result/error contracts.
- `src/DownKyi.Application` depends only on Domain and owns application cancellation plus injectable time contracts.
- `src/DownKyi.Infrastructure` implements Application contracts and never references Desktop, Avalonia, or removed composition frameworks.
- `src/DownKyi.Desktop` owns the framework-neutral Host builder; `DownKyi/Composition/DesktopComposition.cs` owns concrete product registrations through Microsoft DI.
- `DownKyiHost` uses `DisableDefaults=true`; adding configuration providers must be explicit and must not redirect existing database, settings, login, portable-mode, or aria2 session paths.
- There is one Microsoft DI container. Prism/DryIoc, service locator access, global App services, and a second composition root are forbidden.
- Host-independent root XAML must not use `ViewModelLocator.AutoWireViewModel` or `RegionManager.RegionName`; production C# must not reference `ContainerLocator`.
- Long-running operations create a linked scope from `ApplicationCancellation`; caller cancellation stays local, while Host stop cancels every linked operation.

PR 25-29 local result: the real headless Host resolves `MainWindow`, loads complete root XAML, and resolves key ViewModels without loading a Prism runtime. All headless UI tests run on one dedicated Avalonia dispatcher so compositor ownership is deterministic across xUnit worker threads. Architecture tests reject root-view attached composition properties, direct `ContainerLocator` references, deferred video metadata that captures an operation token, and optional JSON envelopes initialized with fake payloads. Download shutdown recovery, settings migration, SQLite resume state, DURL seekability, image source fallback, current-token optional tag loading, endpoint-specific playback envelopes, runtime WBI key refresh, strict Release analysis, format, and all 440 solution tests pass on Windows. Native Linux/macOS execution remains owned by the CI matrix.

## WBI And API Contract Policy

- `IWbiKeyProvider` owns runtime WBI key validity. Persisted `ImgKey` and `SubKey` remain unchanged for data compatibility and are examined once as a startup candidate, not treated as permanently valid configuration.
- Valid keys are published atomically, retained for six hours, and refreshed through one shared task. Canceling one waiter does not cancel the refresh needed by other operations.
- `WbiSign` is a deterministic protocol function: callers supply both keys and the timestamp. It cannot read settings or initialize user state.
- A WBI request may force one refresh and one retry only when Bilibili returns code `-403` from that signed request. A second rejection and all non-WBI/non-`-403` errors propagate with the original code and message.
- Home-page account refresh may update profile and valid WBI keys, but a missing/partial navigation payload cannot erase previously validated keys. Public video parsing cannot depend on login or home-page timing.
- Ordinary video playback uses `data`, bangumi v2 playback uses `result.video_info`, and cheese playback uses `data`. Missing or structurally empty expected payloads are typed contract failures.
- Anonymous `/x/web-interface/nav` may return code `-101` while still carrying public WBI metadata. That exception is endpoint-scoped; every other nonzero API code remains a typed failure.
- Fixed fixtures under `tests/DownKyi.Core.Tests/BiliApi/JsonSamples` cover `BV1U7V66FEiK` video info, page/CID, and playback without using the live Bilibili network.
- `docs/operations/bilibili-api-audit.md` is the endpoint inventory. Any endpoint/envelope change updates it and a deterministic fixture in the same PR; `BilibiliApiInventoryArchitectureTests` enforces coverage.
- `pwsh ./script/audit-bilibili-api.ps1 -ConfirmLive` performs an explicitly requested anonymous probe. It never loads local login data and is not a CI test.
- `pwsh ./script/audit-bilibili-authenticated-api.ps1 -ConfirmAuthenticatedLive` is the separately authorized read-only login probe. It gates on `/nav`, reads only `BILIBILI_TEST_COOKIE` from `~/.codex/.env`, and persists only the allowlisted sanitized artifact.
- Run `pwsh ./script/scan-secrets.ps1` after an authenticated audit. Gitleaks must report zero findings for all tracked and non-ignored untracked candidate files; broad path exclusions are prohibited.

## Analyzer Policy

- Do not add project-wide `NoWarn`, analyzer exclusions, `#nullable disable`, `GlobalSuppressions.cs`, or `.editorconfig` severities of `none` or `silent`.
- Do not add `#pragma warning disable` or `SuppressMessage` merely to make a build pass.
- A minimal external-protocol suppression is allowed only when the protocol requires the algorithm, a contract test proves the requirement, and the code documents why it is not used for passwords or trust decisions.
- Fix diagnostics in this order: security/correctness; async/cancellation/disposal/threading; performance/allocation; public API/collections; naming/globalization/style.
- Before changing fields, properties, collections, or names, inspect JSON/XML serialization, SQLite persistence, Avalonia bindings, reflection, and external protocol contracts.
- Regenerate an inventory from clean-build logs with `script/analyzer-inventory.ps1`; its CSV is the authoritative file-and-line detail, while the Markdown file is the review summary.
- UI-layer awaits that must continue on Avalonia state use `ConfigureAwait(true)`; reusable Core and background infrastructure use `ConfigureAwait(false)`. xUnit test bodies retain the test scheduler with `ConfigureAwait(true)`.
- Fire-and-forget entry points must observe faulted tasks and log the base exception. Do not restore a general `catch (Exception)` sink.
- Types that own cancellation sources, processes, HTTP resources, streams, bitmaps, or download services must release them through an explicit `IDisposable` or `IAsyncDisposable` owner.
- Assemblies explicitly declare `CLSCompliant(false)` in `Directory.Build.props`; this satisfies `CA1014` by documenting the current cross-language contract and must not be changed to `true` without first auditing every public API for CLS compliance.

### Approved Minimal Suppressions

Only the following source-local suppressions are approved. Any other suppression requires the same contract evidence and an update to this section.

| Rule | Location | Reason | Guard | Removal owner |
| --- | --- | --- | --- | --- |
| `CA5351` | `DownKyi.Core/BiliApi/Sign/WbiSign.cs` | Bilibili WBI defines `w_rid` as MD5 of the canonical query plus mixin key. It is an external request-signing format, not password storage or a local trust decision. | `WbiSignTests.EncodeWbiMatchesProtocolVector` | Remove only if Bilibili replaces WBI. |
| `CA5351` | `DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs` | Read-only migration of settings written by DownKyi 1.0.20 and earlier. It cannot encrypt new data; successful reads are immediately rewritten through the current JSON settings writer. | `LegacySettingsDecryptorTests.DecryptReadsLegacySettingsFixture` | Remove only after an explicit migration-window decision includes release telemetry and user-data recovery guidance. |

Both suppressions cover only the algorithm construction or one-shot hash call. Expanding their scope, reusing them for credentials/integrity, or adding another weak-crypto caller is prohibited.

## External Binaries

Release packaging downloads aria2 and FFmpeg from the scripts in `script/`.

- `script/aria2.ps1` and `script/aria2.sh` manage aria2 assets.
- `script/ffmpeg.ps1` and `script/ffmpeg.sh` manage FFmpeg and ffprobe assets.
- Windows and Linux packages prefer FFmpeg builds with hardware encoders. Windows x86 uses the pinned yt-dlp FFmpeg build because the former compact archive omitted ffprobe.
- macOS packages prefer builds that expose VideoToolbox when available.
- Every script resolves the manifest, download directory and binary output
  relative to its own file, so it must work when invoked from the repository
  root or from `script/`.
- `script/assets/external-assets.json` is the only URL/checksum owner. Use an
  immutable release tag, never a mutable `latest` asset. The scripts accept an
  archive only after TLS validation, a successful HTTP status and its SHA-256
  matching the manifest.
- aria2 manifest entries record the official base commit, exact independent
  DownKyi source commit, canonical patch digest, immutable build commit,
  required feature marker, archive digest and extracted binary digest. The
  installer writes the verified binary digest beside `aria2c`; runtime verifies
  that sidecar before process creation and then verifies the feature over RPC.
  These controls identify source and artifact content but do not by themselves
  prove reproducible builds, an SBOM or signed provenance. Current evidence and
  residual risk are maintained in `docs/operations/aria2-security.md` and
  `docs/operations/aria2-security-baseline.json`.
- `DownKyi.Core` stores the checked-in external asset catalog but does not
  select or copy runtime-specific content. The executable is the sole package
  content owner: it uses the explicit publish target first, otherwise the host
  for local development, then directly includes the selected catalog files.
  `DownKyiAssetRuntimeIdentifier` must never cross a project-reference
  boundary or assign the .NET SDK `RuntimeIdentifier`; cross-target restore
  and publish remain the SDK RID owners.
- Packaged local aria2 RPC listens only on a fresh ephemeral loopback port and
  uses a fresh 256-bit secret. The secret is supplied through a temporary
  restricted config and never appears in process arguments. It receives
  `--stop-with-process` on every OS and also joins a kill-on-close Windows Job
  Object, so an abrupt App termination cannot leave a local child running.
  Custom remote aria2 endpoints are not started or terminated by this owner;
  non-loopback RPC requires HTTPS and does not follow redirects.
- aria2 task headers are per-transfer. Cookie can be attached only to an exact
  HTTPS `bilibili.com` host or subdomain. The actual patched transfer engine
  rejects scheme downgrade and credential-bearing cross-origin redirects before
  another request is emitted. There is no process-global Cookie header and no
  production certificate-validation or HTTPS-downgrade switch.

When updating an external binary:

1. Update the independent source commit and mechanically regenerate the
   normal-context canonical patch from the fixed official base.
2. Verify the patch digest, `git apply --check`, actual apply, applied-source
   `git diff --check` and exact source tree equality in the static build repo.
3. Build all six RIDs from the immutable commit and record archive and binary
   SHA-256 values in `script/assets/external-assets.json`.
4. Invoke the installer from the repository root and verify its binary sidecar,
   then run the `aria2-tls-security` matrix for all six RIDs and inspect every
   sanitized report.
5. Confirm `ffmpeg -hide_banner -encoders` lists the expected hardware encoder on a capable machine.
6. Keep fallback behavior intact; missing GPU support must not block normal downloads.

## Release Tag Validation

Before pushing a release tag:

1. Confirm `version.txt` matches the planned tag.
2. Manually dispatch `.github/workflows/build.yml` on the release commit and require all Windows, Linux, and macOS release-gate/package jobs to pass.
3. Confirm each uploaded publish manifest contains non-empty DownKyi, aria2, FFmpeg, and ffprobe binaries with SHA-256 values and the expected application version.
4. Run the quality commands from the dependency section and `git diff --check`.
5. Review `README.md` and `CHANGELOG.md` for user-visible changes.
6. Push `main`, then push the `v*` tag so the same workflow recreates the validated packages.
7. Verify generated packages, per-package `.sha256` files, and publish manifests are attached to the release.

`script/validate-publish-output.ps1` is the common package-content gate. It also rejects a runtime that drops the Fluent theme, restores the Simple theme, omits ffprobe, or publishes a mismatched assembly version. Do not replace it with a file-exists check in only one platform job.

## Regression Checklist

Use this checklist for download, parsing, and exit-related changes:

- Start the app, close it from the window button, and confirm the process exits.
- Reopen the app after closing and confirm the main window appears.
- Parse BV, AV, bangumi, and cheese links.
- Select one item, multiple parts, and all items, then add them to downloads.
- Cancel the directory picker and confirm no task is added.
- Pause, close, reopen, and confirm large tasks resume rather than restart.
- Delete an active large download and confirm media files and `.aria2` / `.download` sidecars are removed.
- Download subtitles and confirm SRT time codes are correct.
- Export diagnostic logs and confirm local user paths, cookies, tokens, and sensitive URLs are redacted.

## Canonical Resource Naming

The default language resource lives at `src/DownKyi.Desktop/Languages/Default.axaml`, and the FFmpeg runtime namespace is `DownKyi.Core.FFmpeg`. Architecture tests reject the historical `Languanges` resource spelling and `FFMpeg` source-directory casing so packaging and case-sensitive platforms cannot drift.
