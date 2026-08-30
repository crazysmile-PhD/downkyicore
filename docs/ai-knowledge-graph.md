# AI Knowledge Locator

This is a small topic-to-authority locator. It records only high-value
boundaries that help an Agent answer: "If I change this, where is the real
owner?"

It is not a class inventory, call graph, test catalog, backlog, policy mirror or
work-status database. Query source, project metadata, tests and workflows on
demand for mechanically derivable detail.

## Dependency Direction

Architecture intent and compatibility commitments are owned by
[ARCHITECTURE.md](../ARCHITECTURE.md). Actual project references and source
topology are owned by the project files and guarded by
[ProjectDependencyTests.cs](../tests/DownKyi.Architecture.Tests/ProjectDependencyTests.cs).

```text
DownKyi executable -> DownKyi.Desktop
DownKyi.Desktop -> DownKyi.Application -> DownKyi.Domain
DownKyi.Infrastructure -> DownKyi.Application + DownKyi.Domain
DownKyi.Core -> DownKyi.Application
```

Target direction and current compatibility exceptions must not be inferred from
this abbreviated diagram; inspect the architecture owner and current project
references.

## High-Value Authority Nodes

### Application composition and lifecycle

- Authority: [DownKyi.Desktop composition](../src/DownKyi.Desktop/Composition/)
  and [lifecycle ownership policy](testing/assembly-lifecycle-owners.json).
- Stable invariant: the executable delegates once to the Desktop-owned
  Avalonia/Generic Host lifecycle; shutdown awaits owned work and flushes.
- Hazard: a second Host, dispatcher, process owner or process-wide callback can
  leave foreground work after tests or the UI report completion.
- Executable guard:
  [AssemblyLifecycleArchitectureTests.cs](../tests/DownKyi.Architecture.Tests/AssemblyLifecycleArchitectureTests.cs).
- Detail: [assembly-lifecycle-stability.md](testing/assembly-lifecycle-stability.md).

### Process ownership, diagnostics and restart

- Authority: [process supervision](../tools/DownKyi.ProcessSupervision/) owns
  launched-child lifecycle; [process lifecycle design](design-docs/process-lifecycle-ownership.md)
  owns the cross-platform identity, deadline and lifetime-domain intent.
- Stable invariant: one OS-backed owner establishes containment before target
  authorization and consumes one caller-created monotonic budget through reap,
  quiescence and stream drain.
- Hazard: PID/PPID, process names, command syntax and diagnostic observers are
  evidence, not authorization, identity, membership or cleanup authority; a
  fallback owner or fresh deadline breaks fail-closed lifecycle semantics.
- Executable guard:
  [AssemblyLifecycleArchitectureTests.cs](../tests/DownKyi.Architecture.Tests/AssemblyLifecycleArchitectureTests.cs).
- Detail: [process-lifecycle-ownership.md](design-docs/process-lifecycle-ownership.md).

### Download state and persistence

- Authority: [Domain downloads](../src/DownKyi.Domain/Downloads/) for legal
  state and [Application download contracts](../src/DownKyi.Application/Downloads/)
  for commands; Infrastructure owns durable adapters.
- Stable invariant: normal runtime mutates durable download state through the
  Domain aggregate and application service, then publishes only committed
  snapshots.
- Hazard: reconstructing Domain state from UI projections creates competing
  state owners and can lose resume identity.
- Executable guard:
  [DownloadTaskStateMachineTests.cs](../tests/DownKyi.Domain.Tests/DownloadTaskStateMachineTests.cs).
- Detail: [ARCHITECTURE.md](../ARCHITECTURE.md#目前下載資料流).

### Download output, retry and cleanup

- Authority: the existing download task state, transfer coordinator and output
  owners under [Desktop download services](../src/DownKyi.Desktop/Services/Download/)
  and [Core media runtime](../DownKyi.Core/FFmpeg/).
- Stable invariant: cancellation and every pre-commit failure preserve retry
  checkpoints; destructive cleanup acts only on output owned by that task.
- Hazard: path discovery, extension scans or process-local registries do not
  establish durable output ownership.
- Executable guard:
  [DownloadArtifactStageTests.cs](../tests/DownKyi.Tests/DownloadArtifactStageTests.cs).
- Detail: [review-invariant-policy.md](testing/review-invariant-policy.md#file-output-ownership).

### Navigation and desktop projections

- Authority: [Desktop navigation](../src/DownKyi.Desktop/Platform/AvaloniaNavigationService.cs) and
  Desktop-owned ViewModels/projections.
- Stable invariant: typed navigation identity owns route meaning; back
  navigation restores existing instances before using a typed parent fallback.
- Hazard: duplicated numeric/route maps or mutable shared icon geometry create
  competing identity and UI state.
- Executable guard:
  [BackNavigationTests.cs](../tests/DownKyi.Tests/BackNavigationTests.cs).
- Detail: [ARCHITECTURE.md](../ARCHITECTURE.md#目前導航與-userspace-資料流).

### Bilibili and WBI contracts

- Authority: [Bilibili API adapters](../DownKyi.Core/BiliApi/) plus the
  [endpoint audit](operations/bilibili-api-audit.md).
- Stable invariant: endpoint, envelope, WBI, cookie and callback assumptions
  remain explicit and fixture-backed; live probes never replace deterministic
  contracts.
- Hazard: missing, null, empty, malformed and nonzero API responses must not
  collapse into fabricated success. Cancellation tests using loopback transport
  synchronize on request receipt; fixed delays can cancel before acceptance and
  manufacture false zero-request evidence.
- Executable guard:
  [BilibiliApiInventoryArchitectureTests.cs](../tests/DownKyi.Architecture.Tests/BilibiliApiInventoryArchitectureTests.cs).

### Settings, storage and compatibility

- Authority: [settings storage](../DownKyi.Core/Storage/) and the tested
  persistence/migration implementations.
- Stable invariant: existing settings JSON, SQLite rows, unfinished tasks,
  transfer identity and resume data remain readable unless an approved
  migration explicitly changes the contract.
- Hazard: derived defaults, synchronous rewrites or migration without backup
  can overwrite a newer or recoverable user state.
- Executable guard:
  [SettingsArchitectureTests.cs](../tests/DownKyi.Architecture.Tests/SettingsArchitectureTests.cs).
- Detail: [ARCHITECTURE.md](../ARCHITECTURE.md#相容性不變量).

### Logging and diagnostics

- Authority: [Infrastructure logging](../src/DownKyi.Infrastructure/Logging/)
  implements Application diagnostic contracts.
- Stable invariant: redaction occurs before persistence or recent buffering;
  flush/disposal failures remain observable. Flush completion means every
  accepted record crossed the bounded persistence barrier, and rotation checks
  cannot be deferred until after one oversized wrapper batch.
- Hazard: a second sink, global logger, unbounded queue or raw sensitive value
  bypasses the single redaction and lifecycle owner.
- Executable guard:
  [ApplicationLogProviderTests.cs](../tests/DownKyi.Infrastructure.Tests/ApplicationLogProviderTests.cs).

### aria2, FFmpeg and packaged assets

- Authority: [external-assets.json](../script/assets/external-assets.json) owns
  asset identity/checksums; repository scripts and workflows own acquisition
  and package verification.
- Stable invariant: immutable assets are checksum-verified, packaged aria2 is
  process-owned and credential-safe, and FFmpeg publishes only validated output.
- Hazard: mutable upstream aliases, credential-bearing redirects, nested retry
  budgets or cleanup before validation break security and resume semantics.
  Asset-selection RID must not leak through project references, and no package
  content or permission may change after the final macOS app signing boundary.
- Executable guard:
  [AriaSecurityTests.cs](../tests/DownKyi.Tests/AriaSecurityTests.cs).
- Design detail:
  [aria2-rpc-client-ownership.md](design-docs/aria2-rpc-client-ownership.md).
- Human procedure: [maintenance.md](maintenance.md#external-binaries).

### Test platform and central runner

- Authority: each test project's `DownKyiTestPlatforms` metadata,
  [central runner source](../tools/DownKyi.CentralTestRunner/) and
  [test-runner-policy.json](testing/test-runner-policy.json).
- Stable invariant: the central runner exclusively owns project/platform
  policy, canonical invocation, one-shot authorization and TRX semantics;
  process supervision exclusively owns test-child lifecycle and its single
  caller-created transition budget.
- Hazard: direct solution-wide `dotnet test`, raw process fallbacks or treating
  exit code as the test result bypasses platform, authorization or result
  authority. Command syntax is not an authorization boundary.
- Executable guard:
  [CentralTestRunnerOwnershipTests.cs](../tests/DownKyi.Architecture.Tests/CentralTestRunnerOwnershipTests.cs)
  and [test-project.ps1](../script/test-project.ps1).
- Detail: [testing/README.md](testing/README.md) and
  [process-lifecycle-ownership.md](design-docs/process-lifecycle-ownership.md#central-test-execution-boundary).

### Review invariants

- Authority: [review-invariant-policy.md](testing/review-invariant-policy.md)
  owns methodology; [review-invariant-corpus.json](testing/review-invariant-corpus.json)
  owns executable invariant-to-test mapping.
- Stable invariant: findings are traced to the earliest shared owner and
  guarded by state-space or transition evidence plus a fail-closed proof.
- Hazard: source-string checks or one-example patches can preserve the reported
  symptom while leaving the failure family open.
- Executable guard:
  [ReviewInvariantCorpusTests.cs](../tests/DownKyi.Architecture.Tests/ReviewInvariantCorpusTests.cs).

### Verification, release and rollback

- Authority:
  [verification-and-rollback.md](operations/verification-and-rollback.md).
- Stable invariant: evidence belongs to one exact commit and compatible runner
  metadata; tags are immutable and rollback preserves user data contracts.
- Hazard: copying command lists or CI matrices into downstream documents causes
  stale gates and false completion claims.
- Executable guard: [quality.yml](../.github/workflows/quality.yml) and
  [build.yml](../.github/workflows/build.yml).

## Query On Demand

Use repository queries instead of extending this file:

- types, callers and implementations: search source and project references;
- tests and platform ownership: inspect `*.Tests.csproj` and run the repository
  platform selector;
- package/SDK versions: inspect repository metadata;
- CI and package matrices: inspect workflows;
- current branch, PR, run or release state: inspect Git and GitHub;
- completed work and historical evidence: inspect Git history, PRs and workflow
  artifacts.

Update this locator only when a high-value authority moves, an architecture
boundary changes, or an existing locator becomes invalid. Do not add transient
status, inventories or duplicated policy prose.
