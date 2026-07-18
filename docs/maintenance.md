# Maintenance Guide

This document records the project maintenance routine for dependencies, external binaries, release validation, and regression checks.

## Dependency Updates

1. Update managed package versions only in `Directory.Packages.props`.
2. Run `dotnet restore ./DownKyi.sln`.
3. Run `dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental -p:TreatWarningsAsErrors=true -p:CodeAnalysisTreatWarningsAsErrors=true -p:EnableNETAnalyzers=true -p:AnalysisMode=All -p:EnforceCodeStyleInBuild=true`.
4. Run `dotnet test ./DownKyi.sln -c Release --no-restore --no-build`.
5. Run `dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive`.
6. Run `dotnet package list --project ./DownKyi.sln --deprecated` and review the report.

Avoid mixing package updates with large refactors unless the refactor is required by the dependency change.

## CI Policy

Pull requests are guarded by `.github/workflows/quality.yml`:

- format check with `dotnet format --verify-no-changes --verbosity diagnostic`
- Windows, Linux, and macOS Release builds
- compiler and all `AnalysisMode=All` CA diagnostics treated as errors
- unit tests with uploaded TRX reports
- transitive vulnerable package audit
- deprecated package report

The repository always uses the supported `AnalysisMode=All` value. The pre-fix baseline is 1,654 unique diagnostics across 71 CA rules; see `docs/analyzer-baseline.md` and `docs/analyzer-baseline.csv`. `CodeAnalysisTreatWarningsAsErrors=true` is the repository default. Every cleaned rule is also pinned to `error` in `.editorconfig`, preventing a future SDK severity change from reopening the baseline. The before/after inventory and retained exceptions are recorded in `docs/analyzer-cleanup-report.md`.

Current analyzer result: zero unhandled CA diagnostics. All 77 cleaned rules are enforced as errors, and the full solution defaults to `CodeAnalysisTreatWarningsAsErrors=true`. Public fields were converted only after checking JSON names, Avalonia bindings, inheritance, and download lifecycle ownership. Indexable collections now use direct indexing without changing empty-list behavior, and property/JSON names use compile-time `nameof` where the wire value is identical. Executable-only application, UI, service, model, and helper types are internal; clean Release compilation verifies Avalonia XAML can still construct its backing types. Public NFO XML contracts remain in Core because `XmlSerializer` requires public root/member types; namespace, XML names, collections, and serialized shape are covered by round-trip tests. Raw Bilibili/aria2 addresses retain string storage and exact JSON keys; login QR and redirect consumers validate absolute `Uri` values at the boundary, while protocol-relative media addresses remain supported. Benchmark cases live in the public, non-sealed `DownKyi.BenchmarkCases` assembly because BenchmarkDotNet generates derived types through reflection; the runner remains internal and validation confirms a result row exists. Async commands use the protected can-execute raiser, dialogs complete typed results, and user-space tab payloads travel through `AppNavigationRequest.Parameter`. Diagnostic hashes use uppercase SHA-256 fragments, NFO booleans use lowercase literals, and FFmpeg cleanup failures use the shared injected logger without duplicate terminal output. JSON/XML/SQLite contracts, enum numeric values, ordinal protocol comparisons, and DURL `Order` identity are all guarded by tests.

## Download Persistence Policy

- `src/DownKyi.Domain/Downloads` owns immutable task identity, lifecycle, progress, transfer, output, failure, and completion state.
- `IDownloadTaskStore` is the only durable download contract. Infrastructure implementations must use async APIs and honor cancellation.
- `SqliteDownloadTaskStore` is the sole owner of download SQL and storage JSON. `DownloadTaskProjectionStore` maps immutable stored tasks to existing `DownloadingItem` / `DownloadedItem` UI projections without owning SQL.
- Use short pooled connections, WAL, optimistic task versions, and transactions. Never restore a process-wide SQLite connection, global database lock, `Task.Run` database wrapper, or offset-based history scan.
- Every schema migration must create a SQLite backup before DDL, execute in one transaction, update `user_version` only on success, and have a rollback test.
- Malformed rows are quarantined individually. Diagnostics may include source table, record ID, field, and a fixed reason; never include raw JSON, full paths, cookies, or URLs.
- Startup loads every unfinished task and the newest 100 history records after shell creation. Remaining history uses keyset pages outside the first-screen path.
- State transitions persist immediately. High-rate progress may use `DownloadProgressWriteBehind`, whose pending task count and wake channel are bounded and whose shutdown path flushes accepted writes.
- The SQLite native bundle is `SQLite3MC.PCLRaw.bundle`. Any update must pass `LegacySqlCipherCompatibilityTests` against the committed SQLCipher v4 fixture before merge.

PR 03-06 result: legacy GID, partial-file maps, completed asset keys, paused state, progress, task identity, and history survive reopen. Completion moves from active state to history in one transaction. The removed deprecated SQLCipher provider was replaced only after the current cross-platform provider opened the old encrypted fixture and rejected a wrong password. Release build, all tests, isolated App startup/close, Linux x64/arm64 and macOS x64/arm64 cross-RID builds, deprecated-package audit, and vulnerable-package audit passed locally.

## Download And Media Runtime Policy

- Queue consumption uses a bounded Channel and fixed workers. Do not restore per-item task spawning or synchronous persistence callbacks.
- Built-in and aria2 transfers share key, resume, integrity, and persistence behavior. Custom aria2 is a backend selection, not a copied workflow.
- `DownloadArtifactWriter` owns cover, subtitle, danmaku, and NFO output. `DownloadTaskStateWriter` owns projection persistence and recovery writes; do not move these details back into `DownloadPipeline`.
- Pause and process shutdown preserve partial/resume files. Explicit task deletion removes generated media and `.aria2` / `.download` sidecars.
- Multi-segment DURL identity includes `DURL.Order`, input is sorted by that order, and concat never starts with stream copy.
- FFmpeg operations use `FfmpegProcessRunner`, bounded concurrency, cancellation, timeout, captured stderr, and process-tree cleanup. Hardware encoding is attempted when available, with CPU fallback kept for success rate.
- A multi-segment output is complete only after ffprobe confirms a video stream, expected duration, and successful middle/tail seek decoding. Invalid partial output is deleted.
- Bilibili requests use the typed `BilibiliHttpClient` registered through `IHttpClientFactory`. The static `WebClient` exists only as a compatibility facade while legacy synchronous API callers are migrated.
- HTTP 401/403 and API schema rejection are non-retryable, 429 honors bounded `Retry-After`, cancellation is never retried, and empty/HTML/malformed responses fail visibly.

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
- `ApplicationLogProvider` is the single file sink and redaction boundary. Do not create another log queue, file writer, or export sanitizer.
- Logging scopes carry correlation, download-task, or child-process context; messages must not contain raw cookies, sensitive query values, account IDs, email addresses, or full personal paths.
- The writer queue and recent-event buffer stay bounded. A full queue may drop an entry and increments the diagnostic drop counter; logging must never block a download or UI thread.
- Application shutdown must await `FlushAsync` and `DisposeAsync`. Explicit flush also releases the active file handle so the Log page can open it immediately on Windows.
- Writer initialization or persistence failures must reach the caller of `FlushAsync`; do not silently report a successful flush.
- Files use UTC `yyyy-MM-dd` directories and JSONL records. Rotation defaults to 32 MiB, hard retention to seven days, and the storage safety cap to 512 MiB; maintenance protects the active file and runs at startup, hourly, day change, rotation, and before export.
- Diagnostic export writes a redacted JSON manifest plus bounded events. Metrics expose capacity ratio, age/capacity deletion counts, and bytes/events written; capacity changes require deterministic retention evidence first.

Logging changes must pass `ApplicationLogProviderTests`, the Host smoke test, and the full Release build with `AnalysisMode=All`.

## Desktop Theme Policy

- Desktop uses `Avalonia.Themes.Fluent` and the Fluent DataGrid theme. Do not reintroduce the Simple theme or load two control themes in the same App.
- Shared typography, spacing, radius, elevation, control-height, and progress-thickness values live in `DownKyi/Themes/DesignTokens.axaml`.
- `ThemeDefault.axaml` retains both `Default` and `Dark` color dictionaries. Theme work must preserve keyboard focus, high-DPI sizing, and existing localized resources.
- Download, history, and favorites lists must retain `VirtualizingStackPanel`; styling changes cannot trade large-list responsiveness for visual uniformity.
- Theme changes require `UiThemeArchitectureTests`, real Host XAML smoke, and an isolated packaged-App startup on Windows. Native Linux/macOS construction remains enforced by the CI matrix.

PR 30-32 local validation: strict `AnalysisMode=All` Release build completed with zero warnings, all 468 tests passed, format verification changed 0/719 files, and vulnerable/deprecated package audits were empty. `actionlint` accepted the quality, system-baseline, and release workflows. The isolated Windows x64 quick system suite produced all shell, restore, SQLite, transfer, UI, CPU-FFmpeg, and NVENC scenarios with complete environment metadata. A real `DownKyi-1.0.32-1.win-x64.zip` was built from checksum-verified external archives; its extracted publish tree passed version/binary/theme validation, created the main window from an isolated data directory, and terminated its aria2 child after forced parent exit. Native package validation remains pending on the manually dispatched cross-platform `Build` workflow before this PR is complete.

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
- Ordinary video playback uses `data`, bangumi playback uses `result`, and cheese playback uses `data`. Missing or structurally empty expected payloads are typed contract failures.
- Fixed fixtures under `tests/DownKyi.Core.Tests/BiliApi/JsonSamples` cover `BV1U7V66FEiK` video info, page/CID, and playback without using the live Bilibili network.

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
- Packaged local aria2 RPC listens only on loopback. It receives `--stop-with-process` on every OS and also joins a kill-on-close Windows Job Object, so an abrupt App termination cannot leave a local child running. Custom remote aria2 endpoints are not started or terminated by this owner.

When updating an external binary:

1. Update the source URL and version in the matching script.
2. Update the expected checksum in the script.
3. Verify the script locally for at least one target platform.
4. Confirm `ffmpeg -hide_banner -encoders` lists the expected hardware encoder on a capable machine.
5. Keep fallback behavior intact; missing GPU support must not block normal downloads.

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

## Historical Naming

The `Languanges` resource folder keeps its historical spelling for now because Avalonia resources and packaging scripts can depend on current paths. Rename it only in a dedicated UI resource cleanup PR with resource-path validation.
