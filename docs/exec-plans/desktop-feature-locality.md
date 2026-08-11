# Desktop Feature Locality Execution Plan

Status: deferred until v1.1.1 release blockers are complete
Design owner: `../design-docs/desktop-feature-locality.md`
Evidence baseline: `912949735733c986bcfeefaa4300a5fdb25c907e`
Baseline date: 2026-08-09
Last reviewed: 2026-08-11

## Goal

Reduce Desktop routed-feature change radius by removing duplicated numeric
Shell identity and adding a complete AppRoute-to-ViewModel-to-DI-to-View Gate.
Retain the existing typed navigation, Microsoft DI and Avalonia presentation
owners.

## Scope Control

In scope:

- route-manifest completeness and adversarial Gate evidence;
- Settings, DownloadManager, Toolbox, MySpace, Friends and UserSpace numeric
  navigation identity;
- typed Shell-local descriptors and only the payload contracts touched by those
  migrations;
- proven legacy `Tag` or unreachable route cleanup after reachability evidence;
- architecture ratchets that prohibit reintroducing numeric route authority.

Out of scope:

- changes before v1.1.1 release blockers are complete;
- a global FeatureRegistry, second router, second DI container or feature-module
  rewrite;
- broad conversion of every `AppNavigationRequest.Parameter` in the repository;
- settings schema, download runtime, aria2, FFmpeg or persistence refactors;
- deleting `UserSpaceChannel` or legacy `Tag` values without executable proof.

## Baseline Snapshot

Everything in this section is true only for the exact evidence baseline. It is
remeasured at the start of PR A and is not a permanent architecture contract.

| Observation | Baseline evidence |
|---|---|
| Routed identities | 32 `AppRoute` enum members |
| Route mapping | 32 switch arms in `GetViewModelType` |
| Production DI | 32 routed ViewModel registrations |
| Presentation | 32 ViewModel DataTemplates in `App.axaml` |
| Direct CI coverage | route-to-ViewModel existence and uniqueness |
| Representative smoke | Host/XAML plus selected Index, VideoDetail, DownloadManager, Network and UserSpace Favorites ViewModels |
| Missing direct Gate | all-route DI resolution and all-route View/DataTemplate completeness |
| Simple numeric Shells | Settings, DownloadManager and Toolbox |
| Stateful numeric Shells | MySpace, Friends and UserSpace |
| `UserSpaceChannel` | structurally mapped/resolvable; no normal production UI banner producer found |
| Legacy `Tag` | 37 declarations; some still feed diagnostic or `PageName` compatibility consumers |
| Planning estimate | 24-38 unique files across four PRs; not a threshold or promise |

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

### PR A - Route Manifest Completeness Gate

Branch: `refactor/locality-a-route-manifest`

1. Remeasure the baseline against then-current `main`.
2. Add an executable contract that enumerates every `AppRoute`, proves exactly
   one ViewModel mapping, resolves each mapped ViewModel from production DI and
   proves exactly one presentable View mapping.
3. Add adversarial/self-test evidence showing missing DI, missing View mapping,
   duplicate mapping and an unenumerated route fail closed. Do not rely on file
   names, string markers or current enum counts.
4. Inventory known Shell entry producers and make `UserSpaceChannel`
   reachability explicit. Do not delete or restore the feature in this PR.
5. Register the invariant in the existing review corpus only after the test
   class exists and is executed by the current runner.

Acceptance: no product behavior change; all current routes pass the complete
manifest proof; mutation/adversarial fixtures demonstrate the Gate fails for
each missing relationship.

### PR B - Simple Shell Descriptor Migration

Branch: `refactor/locality-b-simple-shells`

1. Introduce the smallest Shell-local typed entry shape proven by Settings.
2. Migrate Settings, DownloadManager and Toolbox in isolated commits.
3. Remove each migrated integer-to-route switch and duplicate identity source.
4. Preserve order, title/icon bindings, selected/default tab and command state.

Acceptance: the three Shells navigate through entry routes, contain no numeric
route mapping and pass focused selection/navigation regressions.

### PR C - Stateful Shell Migration

Branch: `refactor/locality-c-stateful-shells`

1. Reuse the proven entry pattern for MySpace, Friends and UserSpace.
2. Replace `SelectedStatus`, `SelectedPackage`, `friendId` and banner-ID route
   authority with typed identity and only the required typed payloads.
3. Preserve same-MID state, back-navigation instance identity, selection,
   API-generated entries and cancellation behavior.
4. Do not convert unrelated navigation payloads.

Acceptance: no visual position or integer value determines a route; stateful
navigation and return-state tests pass on all three Shells.

### PR D - Legacy Cleanup And Ratchet

Branch: `refactor/locality-d-cleanup-ratchet`

1. Remove only `Tag` declarations and compatibility consumers proven unused.
2. Resolve `UserSpaceChannel` according to the product decision and PR A
   reachability evidence.
3. Add Roslyn architecture rules that reject new numeric-to-`AppRoute` authority
   in Shell ViewModels without locking current file, receiver or variable names.
4. Add adversarial fixtures proving equivalent syntax/name rewrites cannot
   bypass the rule.
5. Update current architecture and knowledge graph to the implemented state.

Acceptance: no unowned legacy identity remains; ratchets fail on seeded numeric
route mappings and remain green for presentation-only numeric state.

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
pwsh ./script/test-review-invariants.ps1 `
  -Configuration Release -NoRestore -NoBuild
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release -Iterations 5 -NoBuild -ValidateForensics `
  -ResultsDirectory ./artifacts/assembly-lifecycle/verification
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
pwsh ./script/scan-secrets.ps1
git diff --check
```

The exact-head PR checks must also be green. PR A and PR D additionally prove
their architecture rules with adversarial fixtures; a green source scan without
an executable counterexample is insufficient.

## Documentation Rules

- Stable current ownership belongs in `../ai-knowledge-graph.md` and, after an
  executable architecture change, root `ARCHITECTURE.md`.
- Target rationale remains in the design document.
- Baseline numbers, reachability observations, estimates and task status remain
  here and are remeasured rather than copied into stable documentation.
- `../refactoring-live-plan.md` contains only the current/deferred status and
  links here; it does not duplicate this plan.

## Rollback

PR A is tests/docs only and can be reverted as a unit. PR B and PR C keep
Shell-specific commits so a regressed Shell can be reverted without restoring
numeric mapping elsewhere. PR D cleanup and ratchets remain separate commits.
No rollback changes user data, settings, SQLite records or download state.
