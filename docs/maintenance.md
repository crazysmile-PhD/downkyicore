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

Current PR 02 analyzer result: zero unhandled CA diagnostics. All 77 cleaned rules are enforced as errors, and the full solution defaults to `CodeAnalysisTreatWarningsAsErrors=true`. Public fields were converted only after checking JSON names, Avalonia bindings, inheritance, and download lifecycle ownership. Indexable collections now use direct indexing without changing empty-list behavior, and property/JSON names use compile-time `nameof` where the wire value is identical. Parameterless singleton, settings, zone-list, and log-directory getters now use properties; these cross-project application components are not a supported package API, and no compatibility wrapper or stored-data contract was added. Executable-only application, UI, service, model, and helper types are internal; clean Release compilation verifies Avalonia XAML can still construct its backing types. Public NFO XML contracts were moved unchanged to the Core library because `XmlSerializer` cannot process internal root/member types; the namespace, XML names, collections, and serialized data shape remain unchanged and are covered by round-trip tests. Raw Bilibili/aria2 address values retain string storage and their exact JSON keys, while semantic CLR names use `Address`; login QR/redirect consumers validate absolute `Uri` values at the API boundary, and raw QR addresses are no longer written to terminal or logs. This avoids treating aria2 option values or protocol-relative wire strings as `System.Uri`, and contract tests guard DURL, DASH, login, and aria2 mappings. The request-preparation benchmarks live in the public, non-sealed `DownKyi.BenchmarkCases` library because BenchmarkDotNet generates derived types through reflection, while the `DownKyi.Benchmarks` runner stays internal. Benchmark validation must confirm a result row was produced because BenchmarkDotNet can return exit code zero for an invalid benchmark type. The benchmark deserializes to `JsonElement`, avoiding artificial public DTO contracts that exist only for measurement. The advanced-image wrapper remains private, while the FFmpeg acceleration option item is namespace-level and public because Avalonia-visible ViewModel properties expose it. Async command notification now uses the standard protected event raiser, while dialog closure is a protected action that invokes Prism's existing listener rather than a second event. The user-space tab payload now has a semantic property name while preserving the legacy Prism navigation key. Test identifiers no longer use underscores; renamed protocol enums retain numeric settings values and use explicit aria2/Bilibili wire mappings. The playback facade is now named `VideoStreamApi`; assembly-wide xUnit nonparallelization preserves loopback/process test isolation without public collection-definition types. Favorites API `bv_id` and `bvid` fields now have distinct semantic property names and a JSON contract test. Diagnostic hashes use uppercase hexadecimal, NFO booleans use explicit lowercase literals, and FFmpeg cleanup failures no longer duplicate terminal output already captured by `LogManager`. Aria2, clipboard, logging, and pager notifications use standard event contracts; pager veto uses `CancelEventArgs` and clipboard polling remains desktop-internal. API facades, converters, builders, UI items, and attached-dialog helpers now use role-specific names rather than colliding with namespaces. The collection cleanup preserved JSON array names, SQLite task/resume state, NFO XML collections, and Avalonia collection notification identities; XML contract tests prohibit DTD processing and disable external resolution. Ordinal protocol/path/token comparisons are explicit. DURL descriptors are sorted before selection and use `DURL.Order` as the stable `Id`, producing deterministic keys such as `7_durl`; no BVID or codec hash participates in segment identity. This result was verified by a clean Windows Release build with 106 passing tests and zero warnings, plus zero-warning `linux-x64` and `osx-x64` cross-RID builds. Native Linux/macOS tests run on their matching CI matrix runners.

## Download Persistence Policy

- `src/DownKyi.Domain/Downloads` owns immutable task identity, lifecycle, progress, transfer, output, failure, and completion state.
- `IDownloadTaskStore` is the only durable download contract. Infrastructure implementations must use async APIs and honor cancellation.
- `SqliteDownloadTaskStore` is the sole owner of download SQL and storage JSON. Legacy `DownloadingItem` / `DownloadedItem` projection remains only in `DownKyi/Services/Download/DownloadStorageService.cs` until PR 25-29.
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
- Pause and process shutdown preserve partial/resume files. Explicit task deletion removes generated media and `.aria2` / `.download` sidecars.
- Multi-segment DURL identity includes `DURL.Order`, input is sorted by that order, and concat never starts with stream copy.
- FFmpeg operations use `FfmpegProcessRunner`, bounded concurrency, cancellation, timeout, captured stderr, and process-tree cleanup. Hardware encoding is attempted when available, with CPU fallback kept for success rate.
- A multi-segment output is complete only after ffprobe confirms a video stream, expected duration, and successful middle/tail seek decoding. Invalid partial output is deleted.
- Bilibili requests use the typed `BilibiliHttpClient` registered through `IHttpClientFactory`. The static `WebClient` exists only as a compatibility facade while legacy synchronous API callers are migrated.
- HTTP 401/403 and API schema rejection are non-retryable, 429 honors bounded `Retry-After`, cancellation is never retried, and empty/HTML/malformed responses fail visibly.

PR 07-15 result: Release build completed with zero warnings, 161 tests passed including real FFmpeg/ffprobe seek validation and Host smoke without Prism global container state, format verification passed, and both vulnerable and deprecated package audits were clean. Cross-RID Release builds passed for Windows x86, Linux x64/arm64, and macOS x64/arm64. An isolated Windows process smoke created the main window, accepted close, and exited with code 0 without reading or writing real user data. Native Linux/macOS execution remains owned by their CI runners.

## Settings Persistence Policy

- `ISettingsStore.Current` is the validated immutable read contract. The public mutable `SettingsManager` facade has been removed; production consumers must use `Current` and typed `Update` calls.
- Correlated settings changes use one `Update` call. This prevents another consumer from observing half of a proxy, content-selection, or related multi-field update.
- `SchemaVersion` advances only through `SettingsSchemaMigrator`, one explicit version at a time. A migration must preserve existing JSON property names unless a separately tested compatibility migration is approved.
- Malformed settings are moved to a unique `.invalid-*` backup before safe defaults are persisted. Do not log the payload or its personal path.
- A file with a schema newer than the running application is read only for safe fallback and must remain byte-for-byte unchanged.
- Persistence is debounced, serialized through one async gate, written to a UTF-8 temporary file, flushed, and atomically replaced. Do not restore synchronous whole-file writes or wrap them in `Task.Run`.
- Application shutdown must await `FlushAsync`. Owners that require pending changes to persist during disposal use `DisposeAsync`; synchronous `Dispose` only stops scheduled work.
- The historical DES reader remains read-only. It may decrypt supported old settings once, but no code may use DES to write new data.

Settings changes must pass `SettingsStoreTests`, `SettingsArchitectureTests`, the Host smoke test, and the full Release build with `AnalysisMode=All`.

## Host Composition Policy

- `src/DownKyi.Domain` is framework-free and owns typed result/error contracts.
- `src/DownKyi.Application` depends only on Domain and owns application cancellation plus injectable time contracts.
- `src/DownKyi.Infrastructure` implements Application contracts and never references Desktop, Avalonia, or Prism.
- `src/DownKyi.Desktop` is the only target-architecture project allowed to reference `Microsoft.Extensions.Hosting` or compose Infrastructure with Application.
- `DownKyiHost` uses `DisableDefaults=true`; adding configuration providers must be explicit and must not redirect existing database, settings, login, portable-mode, or aria2 session paths.
- `DownKyi/Composition/LegacyDesktopComposition.cs` and `MainWindow.AttachLegacyRegion` are temporary PR 25-29 bridges. Do not add new dependencies to them.
- Host-independent root XAML must not use Prism `ViewModelLocator.AutoWireViewModel` or `RegionManager.RegionName`; production C# must not reference `ContainerLocator`.
- Long-running operations create a linked scope from `ApplicationCancellation`; caller cancellation stays local, while Host stop cancels every linked operation.

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
| `CA5351` | `DownKyi.Core/Utils/Encryptor/LegacySettingsDecryptor.cs` | Read-only migration of settings written by DownKyi 1.0.20 and earlier. It cannot encrypt new data; successful reads are immediately rewritten through the current JSON settings writer. | `LegacySettingsDecryptorTests.DecryptReadsLegacySettingsFixture` | PR 25-29 removes it after the supported migration window is explicitly closed. |

Both suppressions cover only the algorithm construction or one-shot hash call. Expanding their scope, reusing them for credentials/integrity, or adding another weak-crypto caller is prohibited.

## External Binaries

Release packaging downloads aria2 and FFmpeg from the scripts in `script/`.

- `script/aria2.ps1` and `script/aria2.sh` manage aria2 assets.
- `script/ffmpeg.ps1` and `script/ffmpeg.sh` manage FFmpeg and ffprobe assets.
- Windows x64 and Linux packages prefer FFmpeg builds with hardware encoders.
- macOS packages prefer builds that expose VideoToolbox when available.

When updating an external binary:

1. Update the source URL and version in the matching script.
2. Update the expected checksum in the script.
3. Verify the script locally for at least one target platform.
4. Confirm `ffmpeg -hide_banner -encoders` lists the expected hardware encoder on a capable machine.
5. Keep fallback behavior intact; missing GPU support must not block normal downloads.

## Release Tag Validation

Before pushing a release tag:

1. Confirm `version.txt` matches the planned tag.
2. Run the quality commands from the dependency section.
3. Run `git diff --check`.
4. Review `README.md` and `CHANGELOG.md` for user-visible changes.
5. Push `main`, then push the `v*` tag so `.github/workflows/build.yml` creates packages.
6. Verify generated Windows, Linux, and macOS artifacts are attached to the release.

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
