# Gate 8 Desktop Boundary Execution Plan

Status: completed and integrated
Owner branch: `refactor/desktop-boundary`
Base commit: `550709030210e4fe91113c9b1c05e451a7dc2120`

## Goal

Make `DownKyi.Desktop` the actual owner of Avalonia application lifecycle, Views, ViewModels, UI projections, desktop adapters, resources, and desktop-facing runtime services. Keep `DownKyi.exe` as a minimal bootstrap without changing user data, settings, SQLite records, download IDs, or resumable state.

## Slice 1: Desktop Assembly Ownership

Status: completed

Steps:

1. Move App, Views, ViewModels, UI controls, desktop adapters, resources, and current desktop services from `DownKyi` to `src/DownKyi.Desktop`.
2. Move Avalonia and presentation package references to `DownKyi.Desktop`.
3. Replace the executable startup body with one call to a public Desktop bootstrap.
4. Update resource assembly URIs and test project references.
5. Update path-based architecture tests without relaxing Prism, service-locator, XAML, or dependency-cycle restrictions.

Impact:

- Avalonia resources change assembly owner from `DownKyi` to `DownKyi.Desktop`.
- Existing namespaces and binding type names remain stable.
- The executable retains only packaging metadata and startup.

Verification:

- strict Release build with all warnings as errors.
- Host smoke constructs the real Host, MainWindow, complete XAML, and key ViewModels without referencing the executable.
- Windows test suite and `git diff --check`.

Completion:

- no View, ViewModel, platform adapter, runtime service, or Avalonia resource remains in the executable project.

Evidence:

- `DownKyi` contains one production C# file (`Program.cs`), 14 lines, and only references `DownKyi.Desktop`.
- strict Release build: 0 warnings, 0 errors.
- Host/XAML smoke: 11/11 passed.
- complete solution: 607/607 passed.
- architecture ratchets: 177/177 passed.
- module audit: no UI-collection polling, static HTTP facade, synchronous HTTP, or blocking backoff.
- `git diff --check`: passed.

Rollback:

- revert the ownership move as one commit; no user files or persistent schemas are touched.

## Slice 2: Headless Core

Status: completed

Steps:

1. Keep login HTTP contracts in Core and move QR bitmap rendering to Desktop.
2. Move Bilibili and Zone XAML dictionaries from Core to Desktop.
3. Remove Avalonia and QRCoder package references from Core.
4. Tighten architecture tests from a known-debt baseline to zero Core UI dependencies.

Verification:

- Core build and tests run without Avalonia.
- login QR rendering is covered by Desktop tests.
- full App XAML loads the moved dictionaries.

Completion:

- Core contains no `.axaml`, Avalonia namespace, Bitmap return type, or QR rendering implementation.

Evidence:

- module audit reports 0 Core UI/Avalonia/QRCoder/XAML owners.
- strict Release build: 0 warnings, 0 errors.
- Desktop QR renderer and complete XAML smoke: 13/13 passed.
- Core contract tests: 128/128 passed.
- architecture ratchets: 177/177 passed.
- file/type mismatch baseline decreased from 7 to 5.

Rollback:

- revert the resource/renderer commit; API DTO and HTTP contracts are unchanged.

## Slice 3: Projection And Contract Ownership

Status: completed

Steps:

1. Replace `ImmutableObservableCollection<T>` with owner-only mutable collections exposed as `ReadOnlyObservableCollection<T>`.
2. Move presentation DTOs out of service interfaces or replace them with Application-level records where cross-layer contracts require it.
3. Remove unsupported collection members and presentation-bound contract baseline entries.

Verification:

- collection mutation remains restricted to the UI dispatcher.
- download startup, progress, pause/resume, completion, and history projections remain green.
- architecture tests report zero custom mutable collection consumers and zero presentation-bound service contracts.

Completion:

- Views cannot mutate projected collections.
- Application/service boundaries do not expose `DownKyi.ViewModels` types.

Evidence:

- 14 projection types and `RangeObservableCollection<T>` now live under `src/DownKyi.Desktop/Presentation`.
- `DownloadListState` privately owns mutable backing collections and exposes stable `ReadOnlyObservableCollection<T>` wrappers.
- admission, bootstrap, completion, deletion, replacement, and sorting mutate lists only through owner methods.
- the deleted `ImmutableObservableCollection<T>` has 0 production references and 0 unsupported members.
- service-interface references to `DownKyi.ViewModels` decreased from 3 to 0.
- strict Release build: 0 warnings, 0 errors.
- collection/coordinator/service regression tests: 13/13 passed.
- Desktop Host/XAML/QR smoke: 13/13 passed.
- architecture ratchets: 177/177 passed.
- complete solution: 610/610 passed.
- format verification: 0/791 files changed.
- NuGet vulnerable/deprecated package findings: 0/0.
- Gitleaks candidate scan: 0 findings across 916 files.
- module audit: Core UI 0, ViewModel-bound service contracts 0, custom collection references/unsupported members 0/0.
- `git diff --check`: passed.

Rollback:

- revert the projection/contract commit; durable Domain and SQLite formats remain untouched.

## Final Gate

Run restore, strict Release build, complete tests, format verification, package vulnerability/deprecation audits, module-boundary audit, secret scan, and `git diff --check`. Update `ARCHITECTURE.md`, `ai-knowledge-graph.md`, and `refactoring-live-plan.md`, then require Windows/Linux/macOS quality CI and CodeQL before merge.

Integration:

- PR #92 merged into `refactor/pr-30-32-release-hardening` as `f8e78c9a129047e168f55fcc0afc918eb0eeb4b0`.
- strict quality run `30191251004`, protobuf run `30191250997`, and CodeQL run `30191250992` passed for head `349e8c84`.
- the PR merge ref had zero open CodeQL alerts. GitHub emitted one platform annotation because the required single ownership PR changed 396 files and the PR diff API is capped at 300 files.
