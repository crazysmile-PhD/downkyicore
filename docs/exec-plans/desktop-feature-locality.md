# Desktop Feature Locality Execution Plan

Status: deferred product architecture work
Design owner: `../design-docs/desktop-feature-locality.md`

## Goal

Reduce Desktop routed-feature change radius by removing duplicated numeric
Shell identity and covering the AppRoute-to-ViewModel-to-DI-to-View behavior.
Retain the existing typed navigation, Microsoft DI and Avalonia presentation
owners.

## Scope Control

In scope:

- route-manifest completeness and focused behavior coverage;
- Settings, DownloadManager, Toolbox, MySpace, Friends and UserSpace numeric
  navigation identity;
- typed Shell-local descriptors and only the payload contracts touched by those
  migrations;
- proven legacy `Tag` or unreachable route cleanup after reachability evidence;
- removal of duplicate numeric route authority in the migrated Shells.

Out of scope:

- a global FeatureRegistry, second router, second DI container or feature-module
  rewrite;
- broad conversion of every `AppNavigationRequest.Parameter` in the repository;
- settings schema, download runtime, aria2, FFmpeg or persistence refactors;
- deleting `UserSpaceChannel` or legacy `Tag` values without executable proof.

## Unknowns To Resolve

- Whether `UserSpaceChannel` is a product feature to restore or residue to
  remove. Current source proves absence of the normal UI producer, not product
  intent.
- The exact removable subset of legacy `Tag` declarations.
- The least brittle Avalonia proof for one presentable View mapping per routed
  ViewModel.
- Stateful same-user/back-navigation behavior that requires focused regression
  coverage during PR C.

## Ordered Work

### PR A - Route Manifest Completeness

1. Inspect then-current `main` before editing.
2. Add a focused behavior test that enumerates every `AppRoute`, proves exactly
   one ViewModel mapping, resolves each mapped ViewModel from production DI and
   proves exactly one presentable View mapping.
3. Inventory known Shell entry producers and make `UserSpaceChannel`
   reachability explicit. Do not delete or restore the feature in this PR.

Acceptance: no product behavior change; all current routes pass the complete
mapping behavior test.

### PR B - Simple Shell Descriptor Migration

1. Introduce the smallest Shell-local typed entry shape proven by Settings.
2. Migrate Settings, DownloadManager and Toolbox in isolated commits.
3. Remove each migrated integer-to-route switch and duplicate identity source.
4. Preserve order, title/icon bindings, selected/default tab and command state.

Acceptance: the three Shells navigate through entry routes, contain no numeric
route mapping and pass focused selection/navigation regressions.

### PR C - Stateful Shell Migration

1. Reuse the proven entry pattern for MySpace, Friends and UserSpace.
2. Replace `SelectedStatus`, `SelectedPackage`, `friendId` and banner-ID route
   authority with typed identity and only the required typed payloads.
3. Preserve same-MID state, back-navigation instance identity, selection,
   API-generated entries and cancellation behavior.
4. Do not convert unrelated navigation payloads.

Acceptance: no visual position or integer value determines a route; stateful
navigation and return-state tests pass on all three Shells.

### PR D - Legacy Cleanup

1. Remove only `Tag` declarations and compatibility consumers proven unused.
2. Resolve `UserSpaceChannel` according to the product decision and PR A
   reachability evidence.
3. Update current architecture and knowledge graph to the implemented state.

Acceptance: no unowned legacy identity remains and focused navigation behavior
stays green.

## PR Dependency

`A -> B -> C -> D` is mandatory. Each branch bases on the merged predecessor.
Do not combine these ranges into one PR. A failure in one PR blocks work on its
successor but does not expand that PR into unrelated cleanup.

## Verification

Each PR runs the repository's formal commands from one clean worktree:

```powershell
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:EnableNETAnalyzers=true -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true -p:UseSharedCompilation=false
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
pwsh ./script/scan-secrets.ps1
git diff --check
```

The exact-head PR checks must also be green. Prefer focused behavior tests over
new source scanners or verifier frameworks.

## Documentation Rules

- Stable current ownership belongs in `../ai-knowledge-graph.md` and, after an
  executable architecture change, root `ARCHITECTURE.md`.
- Target rationale remains in the design document.
- Temporary measurements remain in the implementing PR rather than stable
  documentation.

## Rollback

PR A is tests/docs only and can be reverted as a unit. PR B and PR C keep
Shell-specific commits so a regressed Shell can be reverted without restoring
numeric mapping elsewhere. PR D cleanup remains a separate commit.
No rollback changes user data, settings, SQLite records or download state.
