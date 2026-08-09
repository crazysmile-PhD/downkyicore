# Review Invariant Gates

Status: complete

## Goal

Turn recurring Codex/reviewer findings into a small, permanent and executable failure corpus. Fix production behavior only when the overlap audit proves that the reviewed root cause still exists. Do not create one test per historical comment or a second implementation of an existing runtime contract.

## Scope

In scope:

- Group historical findings by root cause and point each invariant at representative existing regression tests.
- Run deterministic failure injection, contract and architecture self-tests in PR CI.
- Keep expensive lifecycle repetition and real-binary/systematic checks in their existing Main or rehearsal profiles.
- Add adversarial fixtures for source-policy and corpus gates so renaming a file, receiver or modifier cannot bypass them.
- Repair SemVer prerelease/build/skip handling and the manual update-dialog contract because the overlap audit confirmed both defects still exist.
- Update `AGENTS.md`, testing policy, the knowledge graph and this plan.

Out of scope:

- New download architecture, backend, path registry, retry policy or lifecycle owner.
- Reimplementing tests already covering durable SQLite output reservations, FFmpeg concurrency, JSON envelopes, request security or process teardown.
- Heavy benchmark thresholds in PR CI.
- Unrelated review backlog or product work.

## Overlap Audit

- Cancellation, durable output reservation, FFmpeg diagnostic concurrency, JSON envelope presence, aria2 redirect/header security and lifecycle teardown already have focused tests. Reuse them through the corpus.
- Output ownership remains a transactional SQLite unique claim coordinated only by `DownloadTaskAdmissionService`. The invariant runner references the existing store/admission tests and does not add a mutable path registry, cache or full unfinished-task scan.
- The existing module policy uses filename and receiver-name-sensitive scans in a few places. Replace the C# gate with Roslyn-backed inspection and adversarial tests; keep the PowerShell audit as reporting evidence.
- Existing version tests strip prerelease identifiers and do not cover build/skip precedence. This is a live product defect, not missing test-only metadata.
- The manual About-page update request omits the required `enableSkipVersion` dialog parameter. This is a live dialog-contract defect.

## Steps

1. Add a machine-readable root-cause corpus and fail-closed validator.
2. Add a filtered runner and invoke it in the existing cross-platform PR matrix.
3. Replace fragile C# source scans with Roslyn inspection and adversarial fixtures.
4. Fix the two confirmed update/version defects and add deterministic regressions.
5. Update Agent/testing/architecture documentation.
6. Run the repository's complete strict Verification.

## Progress

- [x] Root-cause corpus covers all seven test projects without one-comment-one-test duplication.
- [x] Cross-platform PR matrix runs the deterministic corpus.
- [x] Roslyn-backed source rules include adversarial modifier, receiver, filename, root-path and nullable-static-field fixtures.
- [x] SemVer prerelease/build/skip and manual-update dialog defects have deterministic regressions.
- [x] Agent, testing, verification and knowledge documentation are synchronized.
- [x] Complete every local command in Verification.
- [x] Push semantic commits and require exact-head GitHub CI on Windows, Linux and macOS.

Local evidence on 2026-08-09:

- Strict Release build: 0 warnings, 0 errors.
- Review invariant corpus: 12 root-cause invariants, 7 projects, 309 tests.
- Full solution: 884 passed, 0 failed, 1 real-binary integration test skipped locally.
- Module boundary audit: 0 violations.
- Lifecycle ownership: 493 matches, 0 violations.
- Assembly lifecycle: 7 assemblies, 213 phase results, 0 failures.
- Format, Gitleaks 8.30.1, Actionlint, version contract, package vulnerability,
  package deprecation and `git diff --check`: passed.
- GitHub PR #129: Windows/Linux/macOS build and test, six-RID aria2 security,
  assembly lifecycle, format, package audit, protobuf and CodeQL passed.

The Roslyn audit exposed two pre-existing presentation-bound service contracts:
`DownloadManagerCoordinator.cs` and `LegacyUpgradeCoordinator.cs`. They are an
exact non-growth ratchet, not a claim of zero debt, and are outside this CI PR.

## Verification

```powershell
dotnet restore .\DownKyi.sln

dotnet build .\DownKyi.sln `
  -c Release `
  --no-restore `
  --no-incremental `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true `
  -p:UseSharedCompilation=false

pwsh .\script\test-review-invariants.ps1 `
  -Configuration Release `
  -NoRestore `
  -NoBuild
pwsh .\script\test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh .\script\audit-module-boundaries.ps1 `
  -OutputPath .\artifacts\architecture\module-boundary-audit.json
pwsh .\script\audit-lifecycle-ownership.ps1 `
  -OutputDirectory .\artifacts\assembly-lifecycle\ownership
pwsh .\script\test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Iterations 5 `
  -NoBuild `
  -ValidateForensics `
  -ResultsDirectory .\artifacts\assembly-lifecycle\verification
dotnet format .\DownKyi.sln --no-restore --verify-no-changes
pwsh .\script\scan-secrets.ps1
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- .github/workflows/*.yml
dotnet package list --project .\DownKyi.sln --vulnerable --include-transitive
dotnet package list --project .\DownKyi.sln --deprecated
git diff --check
```

Pass means every command exits zero, every corpus entry resolves to a real test class, the filtered run executes at least one test per declared project, all seven test projects pass, and no architecture/self-test can be bypassed by the supplied adversarial fixtures.

## Rollback

Revert the corpus/gate commit and the version-contract commit independently. Do not change user data, SQLite state, download files or settings schema. Existing skip-version values remain string-compatible if the version-contract commit is reverted.
