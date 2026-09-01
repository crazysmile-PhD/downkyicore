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

Solution builds set `CompileUsingReferenceAssemblies=false`. Consumers compile
against completed implementation assemblies instead of the SDK's transient
`obj/<configuration>/<framework>/ref` copies. This keeps a producer's successful
implementation output authoritative and prevents an intermittently invalid
reference assembly from cascading into `CS0009`, `CS0234`, and `CS0246` on
hosted builds. Keep this policy unconditional across platforms; removal requires
an exact-SDK cross-platform stress proof that consumers can safely return to the
generated reference-assembly path.

## CI Policy

### Test Platform Ownership

Every `*.Tests.csproj` must declare `DownKyiTestPlatforms` as an explicit
semicolon-separated subset of `Windows;Linux;macOS`; projects that support all
three list all three, with no implicit default. Native behavioral tests belong
in an OS-owned project. `DownKyi.CentralTestRunner`, reached through
`script/test-project.ps1` and `script/test-solution.ps1`, rejects unknown
projects or platform declarations and runs only projects that include the
current OS. `docs/testing/test-runner-policy.json` records the two necessary
xUnit in-process routing exceptions and their rationale.

The runner records test identity, root process identity and lifecycle events.
PASS discards the recorder; failure preserves bounded stdout/stderr, cleanup
state and one final best-effort process snapshot under
`artifacts/test-flight-recorder`. The snapshot is diagnostic evidence, not a
complete descendant proof. See `docs/testing/README.md` for the executable
owners and focused behavior tests.

Pull requests are guarded by `.github/workflows/quality.yml`:

- format check with `dotnet format --verify-no-changes --verbosity diagnostic`
- Windows, Linux, and macOS Release builds
- compiler and all `AnalysisMode=All` CA diagnostics treated as errors
- unit tests with uploaded TRX reports
- transitive vulnerable package audit
- deprecated package report

Release tags are additionally checked by
`script/validate-release-version.ps1`. `version.txt` must contain one stable
`major.minor.patch` value, and a tag workflow may proceed only when
`GITHUB_REF` equals `refs/tags/v<version>`. The v1.1.0 tag is immutable and its
withdrawn draft must not be republished; the corrective release is v1.1.1.

The repository uses `AnalysisMode=All` with
`CodeAnalysisTreatWarningsAsErrors=true`. Current analyzer inventory and the
approved cleanup record live in `docs/analyzer-baseline.csv`,
`docs/analyzer-baseline.md` and `docs/analyzer-cleanup-report.md`.

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

## Download And Media Runtime Policy

- Queue consumption uses a bounded Channel and fixed workers. Do not restore per-item task spawning or synchronous persistence callbacks.
- New, resumed, and startup-restored work enters through `DownloadTaskQueueGateway` as `DownloadTaskId`. The unbounded admission channel isolates UI/startup callers from worker-channel capacity; only the internal dispatcher waits for bounded worker capacity. Runtime scheduling must never poll `ObservableCollection`.
- New task admission is serialized by the singleton `DownloadTaskAdmissionService`. It selects one normalized base path against authoritative unfinished Domain tasks and current disk outputs, persists it before UI/queue publication, and uses case-insensitive comparison on Windows. Failed retryable tasks retain the reservation; canceled/completed/deleted tasks release active ownership.
- Built-in and aria2 transfers share key, resume, integrity, and persistence behavior. Custom aria2 is a backend selection, not a copied workflow.
- `DownloadArtifactWriter` owns cover, subtitle, danmaku, and NFO output. `DownloadTaskStateWriter` adapts runtime calls to typed Application commands; it cannot accept `DownloadingItem` or persist reconstructed UI state.
- Pause first persists `Pausing`; built-in/aria2 backends preserve partial files and return a paused outcome; the worker confirms `Paused` only after transfer teardown. Process shutdown returns active `Downloading`/`Pausing` tasks to `Queued` without dropping GID, partial-file maps, completed keys, progress, output size, or optimistic version.
- Explicit task deletion persists `Canceled`, stops the physical backend, removes generated media and `.aria2` / `.download` sidecars, then deletes the row. A cleanup failure leaves a retryable canceled record rather than pretending deletion succeeded.
- Multi-segment DURL identity includes `DURL.Order`, input is sorted by that order, and concat never starts with stream copy.
- FFmpeg operations use `FfmpegProcessRunner`, bounded concurrency, cancellation, timeout, captured stderr, and process-tree cleanup. Hardware encoding is attempted when available, with CPU fallback kept for success rate.
- Download mux/concat writes a same-directory temporary output and refuses to overwrite an existing destination. Failure cleans only its temporary file and preserves both foreign destination content and valid source streams; toolbox transforms keep their explicitly separate overwrite contract.
- FFmpeg mux failure returns typed invalid-input evidence. Only a source that a real fail-on-error decode rejects may have its completed transfer key, backend identity, file and sidecars revoked; process startup, timeout, permission, destination and other infrastructure failures preserve reusable source state.
- A multi-segment output is complete only after ffprobe confirms a video stream, expected duration, and successful middle/tail seek decoding. Invalid partial output is deleted.
- Bilibili requests use injected `IBilibiliApiClient` and `IBuvidProvider` ports backed by the Infrastructure `IHttpClientFactory` transport. Endpoint adapters are async; static client state, global configuration and synchronous HTTP compatibility paths are prohibited.
- HTTP 401/403 and API schema rejection are non-retryable, 429 honors bounded `Retry-After`, cancellation is never retried, and empty/HTML/malformed responses fail visibly.

Loopback cancellation tests must synchronize on the server receiving the first request before canceling. Fixed timeouts are not an acceptable substitute because a loaded Windows runner can cancel before socket acceptance and produce a false zero-request failure.
CodeQL uses explicit `manual` build mode so generated and build-resolved C# remains in the high-accuracy full database. `CODEQL_OVERLAY_DATABASE_MODE=none` disables the incompatible incremental overlay optimization; do not switch to buildless `none` mode only to remove an annotation, because that can omit generated code.

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

## Host Composition Policy

- `src/DownKyi.Domain` is framework-free and owns typed result/error contracts.
- `src/DownKyi.Application` depends only on Domain and owns application cancellation plus injectable time contracts.
- `src/DownKyi.Infrastructure` implements Application contracts and never references Desktop, Avalonia, or removed composition frameworks.
- `src/DownKyi.Desktop` owns the framework-neutral Host builder; `DownKyi/Composition/DesktopComposition.cs` owns concrete product registrations through Microsoft DI.
- `DownKyiHost` uses `DisableDefaults=true`; adding configuration providers must be explicit and must not redirect existing database, settings, login, portable-mode, or aria2 session paths.
- There is one Microsoft DI container. Prism/DryIoc, service locator access, global App services, and a second composition root are forbidden.
- Host-independent root XAML must not use `ViewModelLocator.AutoWireViewModel` or `RegionManager.RegionName`; production C# must not reference `ContainerLocator`.
- Long-running operations create a linked scope from `ApplicationCancellation`; caller cancellation stays local, while Host stop cancels every linked operation.

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
- FFmpeg upstream discovery and project-owned immutable mirroring are owned by
  `.github/workflows/update-ffmpeg-assets.yml`; see
  `docs/operations/ffmpeg-asset-mirroring.md`. Production entries must be
  fixed project-owned release URLs, never BtbN/yt-dlp/martin-riedl URLs or
  `latest` aliases.
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
3. For macOS without Apple credentials, require the final app bundle to use ad-hoc signing and pass `codesign --verify --deep --strict` before DMG creation. Record that the resulting DMG is not Developer ID signed, notarized, stapled, or Gatekeeper-trusted.
4. When `MACOS_CERTIFICATE`, `MACOS_CERTIFICATE_PWD`, `APPLE_ID`, `TEAM_ID`, and `APP_SPECIFIC_PASSWORD` are available, additionally confirm macOS x64 and arm64 app notarization, stapling, Gatekeeper assessment, DMG signing, DMG verification, DMG notarization, and final DMG assessment before upload.
5. Confirm each uploaded publish manifest contains non-empty DownKyi, aria2, FFmpeg, and ffprobe binaries with SHA-256 values and the expected application version.
6. Run the quality commands from the dependency section and `git diff --check`.
7. Review `README.md` and `CHANGELOG.md` for user-visible changes.
8. Push `main`, then push the `v*` tag so the same workflow recreates the validated packages.
9. Verify generated packages, per-package `.sha256` files, and publish manifests are attached to the release.

`script/validate-publish-output.ps1` is the common package-content gate. It also rejects a runtime that drops the Fluent theme, restores the Simple theme, omits ffprobe, or publishes a mismatched assembly version. Do not replace it with a file-exists check in only one platform job.

macOS signing is deliberately last-mile and inside-out: managed assemblies and Mach-O files in the app bundle are signed explicitly before the outer app. Non-code publish files are stored under `Contents/Resources` and linked from their host-expected relative paths; `Contents/MacOS` must not contain unsigned regular data files. `script/macos/package.sh` may create the app bundle, copy `Info.plist`, icon and publish output, and apply executable bits to aria2/FFmpeg. No content or permission step may run after `script/macos/sign.sh`; a later mutation invalidates the resource seal. The release workflow verifies and launches the exact app bundle from the completed DMG, not just an earlier signing command.

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
