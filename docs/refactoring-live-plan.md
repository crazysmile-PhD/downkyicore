# DownKyi Live Plan

Status: active
Last updated: 2026-08-01
Current work item: v1.1.1 item 1, aria2 TLS and control-plane security
Current branch: `fix/aria2-tls-certificate-validation`
Current base: `origin/main` at `e2fd83d09b0fa641453fc92912ad238ed5499056`

This file contains only unfinished, blocked or integration-pending work. Accepted
design and completed history belong in `ARCHITECTURE.md`, `docs/design-docs/`,
`docs/maintenance.md` and release notes. The detailed item contract is
`docs/exec-plans/v1.1.1-security-patch.md`.

## Current Item

### v1.1.1 Item 1: aria2 TLS And Control Plane

- [x] Remove the packaged aria2 certificate-validation bypass and every
      production HTTP downgrade path.
- [x] Remove Cookie and RPC secret from aria2 process arguments.
- [x] Replace process-global headers with per-transfer headers and exact HTTPS
      Bilibili credential scope.
- [x] Use `ProcessStartInfo.ArgumentList` and reject header control characters.
- [x] Give each packaged runtime a fresh ephemeral loopback port and 256-bit
      secret; verify the supervised child remains alive before accepting RPC.
- [x] Reject non-loopback plaintext RPC, URI credentials and RPC redirects.
- [x] Make legacy `UseSsl` a read-only migration marker that cannot affect
      runtime and disappears on the next settings write.
- [x] Add typed TLS failure classification and user-visible status.
- [x] Add architecture, settings, RPC, process-argument, header-scope and retry
      regression tests.
- [x] Add a real-binary deterministic TLS suite covering valid trust, resume,
      redirects, RPC lifecycle, unknown/self-signed/expired/future/wrong-host/
      missing-SAN/incomplete-chain failures, app-level retry, actual GET/Range/
      second-request downgrade and sensitive cross-origin redirects.
- [x] Add the six-RID `aria2-tls-security` quality matrix and sanitized report.
- [x] Preserve the original fork and complete mirror bundle; create independent
      `downkyi-aria2` source and `downkyi-aria2-static-build` repositories.
- [x] Generate a normal-context canonical patch from fixed source commit
      `9938788f7e62af0530a1b28ece752e1de1fd0d46`; verify its SHA, apply and exact
      resulting tree separately from ordinary repository `git diff --check`.
- [x] Pin zlib and OpenSSL source URLs and SHA-256 values in `source-lock.json`;
      build jobs do not follow a movable source branch or dependency page.
- [x] Require bundled binary SHA verification before process start and the
      versioned secure-redirect feature for bundled and custom aria2 RPC.
- [x] Complete all six static binary builds, fix their archive/binary SHA-256
      values and publish them from immutable tag `1.37.0-downkyi.2`.
- [x] Pass the actual-transfer TLS/redirect/header suite on all six binaries and
      prove downgrade targets receive zero requests. Windows x64/x86 are
      locally green; all six RIDs passed CI run `30799263926`.
- [x] Pass strict local Release build, all seven tests, format, module audit,
      lifecycle ownership/gate, package audits, secret scan and diff check on
      the final manifest and product diff.
- [x] Commit and push the complete isolated item to its one feature branch.
- [x] Open one Draft PR against `main` and require all six real aria2 RID jobs,
      normal quality jobs and CodeQL to pass on the exact head commit.
- [x] Review uploaded sanitized reports, verify no credential/path leakage and
      update `aria2-security-baseline.json` with exact CI evidence.
- [ ] Resolve all item-1 merge blockers and mark the item complete.

## Next Items

These remain deliberately unstarted until the previous item is complete:

1. PR #120 scope cleanup and merge-blocking findings.
2. Bangumi `ep_id` propagation and playback response contract.
3. Remaining v1.1.1 P1, merge-blocking or core runtime review debt.

Each item must use a separate branch and PR. Do not merge, rebase or copy legacy
architecture wholesale; migrate only valid behavior into current DI, typed
navigation and coordinator boundaries.

## Release Blockers

- All four ordered v1.1.1 items must be complete and integrated into current
  `main`.
- The exact final commit must pass strict quality, CodeQL, Main lifecycle 50,
  complete release rehearsal and Windows/Linux/macOS package validation.
- Settings JSON, legacy SQLite, unfinished tasks, GID, partial-file maps,
  completed keys and resume fixtures must remain compatible.
- The source tree and packages must remain free of Cookie, account data, local
  Config/Logs/Cache/Storage and developer artifacts.
- `v1.1.0` stays immutable. Do not change `version.txt`, tag or publish v1.1.1
  until every blocker is green.

## Verification

Run sequentially in one worktree:

```powershell
dotnet restore ./DownKyi.sln
pwsh ./script/validate-release-version.ps1
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:EnableNETAnalyzers=true -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true -p:UseSharedCompilation=false
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release -Iterations 5 -NoBuild -ValidateForensics `
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

A result is valid only when its runtime, OS, architecture, commit SHA and
dirty-worktree state are recorded. Cross-machine timings are not compared
directly.

## Completion And Rollback

An item is complete only after implementation, tests, documentation, exact-head
CI and review are green. Remove it from this file after merge and promote its
stable facts to architecture/maintenance documentation. Before merge, close its
Draft PR. After merge, revert the entire item commit range without changing user
data formats or reintroducing a security bypass.

Gate 10 is complete only when its formal local lifecycle verification, exact
Main profile and release rehearsal remain green; item 1 must preserve those
already-established lifecycle conditions rather than replacing them.
