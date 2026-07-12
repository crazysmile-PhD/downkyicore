# Maintenance Guide

This document records the project maintenance routine for dependencies, external binaries, release validation, and regression checks.

## Dependency Updates

1. Update managed package versions only in `Directory.Packages.props`.
2. Run `dotnet restore ./DownKyi.sln`.
3. Run `dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental -p:TreatWarningsAsErrors=true -p:CodeAnalysisTreatWarningsAsErrors=false -p:EnableNETAnalyzers=true -p:AnalysisMode=All -p:EnforceCodeStyleInBuild=true` while the analyzer cleanup is active.
4. Run `dotnet test ./DownKyi.sln -c Release --no-restore --no-build`.
5. Run `dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive`.
6. Run `dotnet package list --project ./DownKyi.sln --deprecated` and review the report.

Avoid mixing package updates with large refactors unless the refactor is required by the dependency change.

## CI Policy

Pull requests are guarded by `.github/workflows/quality.yml`:

- format check with `dotnet format --verify-no-changes --verbosity diagnostic`
- Windows, Linux, and macOS Release builds
- compiler warnings as errors, with every `AnalysisMode=All` CA diagnostic visible during the cleanup transition
- unit tests with uploaded TRX reports
- transitive vulnerable package audit
- deprecated package report

The repository always uses the supported `AnalysisMode=All` value. The pre-fix baseline is 1,654 unique diagnostics across 71 CA rules; see `docs/analyzer-baseline.md` and `docs/analyzer-baseline.csv`. `CodeAnalysisTreatWarningsAsErrors=false` is temporary, while compiler warnings still block CI. Promote a rule to `error` in `.editorconfig` only after every occurrence is fixed and verified. Set `CodeAnalysisTreatWarningsAsErrors=true` only after all unhandled CA diagnostics reach zero on every required platform.

Current PR 02 checkpoint: 406 unique diagnostics across 9 rules remain. The 65 rules already at zero are enforced as errors: `CA1001`, `CA1002`, `CA1003`, `CA1008`, `CA1012`, `CA1014`, `CA1024`, `CA1030`, `CA1031`, `CA1032`, `CA1034`, `CA1051`, `CA1052`, `CA1055`, `CA1058`, `CA1062`, `CA1063`, `CA1303`, `CA1308`, `CA1309`, `CA1507`, `CA1508`, `CA1513`, `CA1707`, `CA1708`, `CA1711`, `CA1720`, `CA1802`, `CA1805`, `CA1810`, `CA1813`, `CA1816`, `CA1819`, `CA1820`, `CA1822`, `CA1823`, `CA1826`, `CA1829`, `CA1845`, `CA1847`, `CA1849`, `CA1850`, `CA1854`, `CA1859`, `CA1861`, `CA1862`, `CA1864`, `CA1866`, `CA1872`, `CA2000`, `CA2007`, `CA2008`, `CA2025`, `CA2100`, `CA2201`, `CA2211`, `CA2213`, `CA2214`, `CA2227`, `CA2234`, `CA2263`, `CA5351`, `CA5369`, `CA5373`, and `CA5401`. Public fields were converted only after checking JSON names, Avalonia bindings, inheritance, and download lifecycle ownership. Indexable collections now use direct indexing without changing empty-list behavior, and property/JSON names use compile-time `nameof` where the wire value is identical. Parameterless singleton, settings, zone-list, and log-directory getters now use properties; these cross-project application components are not a supported package API, and no compatibility wrapper or stored-data contract was added. The request-preparation benchmark deserializes to `JsonElement`, avoiding artificial public DTO contracts that exist only for measurement. The advanced-image wrapper remains private, while the FFmpeg acceleration option item is namespace-level and public because Avalonia-visible ViewModel properties expose it. Async command notification now uses the standard protected event raiser, while dialog closure is a protected action that invokes Prism's existing listener rather than a second event. The user-space tab payload now has a semantic property name while preserving the legacy Prism navigation key. Test identifiers no longer use underscores; renamed protocol enums retain numeric settings values and use explicit aria2/Bilibili wire mappings. The playback facade is now named `VideoStreamApi`, and xUnit isolation fixtures use group names without changing collection constants. Favorites API `bv_id` and `bvid` fields now have distinct semantic property names and a JSON contract test. Diagnostic hashes use uppercase hexadecimal, NFO booleans use explicit lowercase literals, and FFmpeg cleanup failures no longer duplicate terminal output already captured by `LogManager`. Aria2, clipboard, logging, and pager notifications use standard event contracts; pager veto uses `CancelEventArgs` and clipboard polling remains desktop-internal. The collection cleanup preserved JSON array names, SQLite task/resume state, NFO XML collections, and Avalonia collection notification identities; XML contract tests prohibit DTD processing and disable external resolution. This checkpoint was produced by a clean Release build and 88 passing tests; it is progress evidence, not the final cross-platform zero-warning gate.

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
- `script/ffmpeg.ps1` and `script/ffmpeg.sh` manage FFmpeg assets.
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
