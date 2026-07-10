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

## Analyzer Policy

- Do not add project-wide `NoWarn`, analyzer exclusions, `#nullable disable`, `GlobalSuppressions.cs`, or `.editorconfig` severities of `none` or `silent`.
- Do not add `#pragma warning disable` or `SuppressMessage` merely to make a build pass.
- A minimal external-protocol suppression is allowed only when the protocol requires the algorithm, a contract test proves the requirement, and the code documents why it is not used for passwords or trust decisions.
- Fix diagnostics in this order: security/correctness; async/cancellation/disposal/threading; performance/allocation; public API/collections; naming/globalization/style.
- Before changing fields, properties, collections, or names, inspect JSON/XML serialization, SQLite persistence, Avalonia bindings, reflection, and external protocol contracts.
- Regenerate an inventory from clean-build logs with `script/analyzer-inventory.ps1`; its CSV is the authoritative file-and-line detail, while the Markdown file is the review summary.

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
