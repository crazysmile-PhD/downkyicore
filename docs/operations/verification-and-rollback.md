# Verification, Release And Rollback

This document is the single human procedure owner for formal local
verification, release evidence and rollback. Current branch, PR, run and
completed-work status belong in GitHub and Git, not here.

## Evidence Identity

Before a formal run, record:

```powershell
git status --short --branch
git rev-parse HEAD
```

A result is valid only for its exact commit, dirty-worktree state, runtime, OS
and architecture. Evidence does not transfer to a rebased or rebuilt commit,
and timings from incompatible machines or datasets are not compared directly.

Run build and test phases sequentially in one worktree. Parallel worktrees may
share NuGet caches, but they must not share `bin`, `obj`, test results or
lifecycle artifact directories.

## Canonical Verification

```powershell
dotnet restore ./DownKyi.sln
pwsh ./script/validate-release-version.ps1
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:EnableNETAnalyzers=true -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true -p:UseSharedCompilation=false
pwsh ./script/test-solution.ps1 `
  -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release -Profile Main -NoBuild `
  -ResultsDirectory ./artifacts/assembly-lifecycle/verification
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml | `
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
git diff --check
```

`test-solution.ps1` is the canonical test entry point. It discovers test
projects, validates each `DownKyiTestPlatforms` declaration, selects projects
owned by the current OS and applies
[test-runner-policy.json](../testing/test-runner-policy.json). Do not replace it
with direct solution-wide `dotnet test`.

Lifecycle verification separately proves load, assembly-info, discovery,
execution, fixture teardown and process exit. Total test count is not a
coverage oracle.

Use the smallest focused regression while iterating. Run the complete sequence
when the change affects a formal owner, before publishing a final candidate, or
when the governing test policy requires it. Do not rerun an expensive gate
without a causal reason.

## Release Policy

- Publish only from one clean final commit after required quality, CodeQL,
  lifecycle, rehearsal and package gates pass for that exact commit.
- Recheck every downstream change after its prerequisite or base changes.
- Existing tags are immutable. Do not move, reuse or silently replace a tag.
- Do not change [version.txt](../../version.txt), create a tag or publish a
  release while a required gate or release blocker remains unresolved.
- Source and packages must not contain credentials, account data, local user
  data directories or developer artifacts.
- A migration that changes settings, SQLite, unfinished tasks, transfer
  identity, partial files, completed keys or resume state requires explicit
  compatibility and rollback evidence.

## Release Rehearsal

The release workflow owns its current matrix. The formal lifecycle rehearsal
uses the repository `Rehearsal` profile:

```powershell
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Profile Rehearsal `
  -NoBuild `
  -ResultsDirectory ./artifacts/assembly-lifecycle/release
```

Preserve the machine report, ownership report, raw protocol output and timeout
evidence as workflow artifacts. A successful rerun does not close an
intermittent lifecycle failure without causal owner and teardown evidence.

Before a release tag:

1. Run the canonical verification on the exact candidate.
2. Dispatch the existing release workflow on that commit and require every
   configured gate/package job to pass.
3. Download and validate package manifests, checksums, required binaries,
   version identity, runtime content and user-data exclusions.
4. Confirm macOS trust claims against the exact final app/DMG mode validated by
   the workflow. Ad-hoc signing must not be described as Developer ID,
   notarization, stapling or Gatekeeper trust.
5. Push `main`, then the immutable version tag only after the exact candidate is
   accepted.
6. Verify the release attachments and published checksums.

External asset update details are owned by
[maintenance.md](../maintenance.md#external-binaries). Package shape is guarded
by [validate-publish-output.ps1](../../script/validate-publish-output.ps1), not a
platform-local file-exists check.

## Authorized Live Protocol Evidence

Live Bilibili probes require explicit operator authorization. Use the existing
anonymous or authenticated audit scripts only as described by
[bilibili-api-audit.md](bilibili-api-audit.md). Credentials must enter through
the documented environment boundary, and only sanitized allowlisted metadata
may be persisted. Run the repository secret scan afterward.

## Runtime Evidence

- Real Host/XAML: existing Desktop smoke tests.
- Navigation history: typed navigation tests covering reuse and disposal.
- Download/retry: deterministic fake or loopback transport tests.
- Media output: existing ffprobe seek/decode integration.
- Logs: isolated data roots covering redaction, flush, rotation and export.
- System performance: metadata-complete benchmark artifacts; values from
  different environments are not directly comparable.

## Completion And Rollback

Implementation, focused regression, required formal gates, exact-head CI and
review are separate completion stages. Do not report a later stage complete
from evidence for an earlier one.

Before merge, rollback means closing the draft and deleting only its feature
branch after preserving any requested evidence. After merge, use a
non-destructive revert of the complete change range:

```powershell
git revert <commit-sha>
```

Do not use `git reset --hard` or overwrite a user's worktree. Data migration
changes require an old-schema fixture, backup location, reopen verification,
rollback or forward-repair procedure, and explicit unfinished/resume-state
coverage. XAML or rename rollback reverts the complete ownership change so
resource URI, DI and route references remain coherent.
