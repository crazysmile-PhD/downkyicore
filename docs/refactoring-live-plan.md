# DownKyi Core Live Refactoring Plan

Status: active
Last updated: 2026-07-29
Current group: Gate 10 Windows lifecycle blocker and corrective release
Current branch: `fix/lifecycle-slow-evidence-arm-window`

This file contains only unfinished or not-yet-integrated work. Completed Gate
1-9 detail is retained in `docs/maintenance.md`, design documents and Git
history, not repeated here.

## Current State

- Gate 1-9 are integrated into
  `origin/refactor/pr-30-32-release-hardening` at merge commit `182745e`.
- PR #111 closed the final oversized production owner. Its final head passed
  Windows/Linux/macOS quality run `30424802172` and CodeQL run `30424802168`;
  all seven checks had zero annotations and each platform retained seven
  assembly-named TRX artifacts.
- Latest `main` and the full stacked branch merge cleanly. Integration commit
  `fb8c922` has parents `661a7c4` and `182745e`.
- The pre-version integration commit passes strict `AnalysisMode=All` Release
  build with zero warnings/errors and all 713 tests across seven projects.
- `version.txt` is now the only `1.1.0` source. Generated DownKyi metadata is
  Assembly/File `1.1.0.0` and informational `1.1.0+<commit>`.
- The local version candidate passes strict build with zero warnings/errors,
  all 714 tests, format, module audit, vulnerable/deprecated package audits,
  `git diff --check`, and Gitleaks across 986 candidate files.
- The latest committed candidate authenticated read-only audit passed its navigation gate
  and all 14 allowlisted contracts with HTTP 200, Bilibili code 0, required
  fields present and zero contract drift at `8aa4382`. Gitleaks 8.30.1 then
  reported zero findings across all 986 candidate files.
- PR #112 first-head quality run `30426137294`, protobuf run `30426137279`
  and CodeQL run `30426137276` passed. Each platform uploaded seven distinct
  TRX files; 713 tests executed successfully and the FFmpeg-dependent seek
  integration was the only non-executed CI case.
- Manual package rehearsal `30426554087` then exposed two release-only
  defects: expired BtbN autobuild URLs on Windows x64/Linux and host-derived
  `RuntimeIdentifier` contamination during x64 publication on arm64 macOS.
  Candidate `8aa4382` pins existing immutable FFmpeg releases, resolves all
  asset scripts from their own directory, uses one manifest on every OS, and
  separates asset selection from the SDK runtime identifier.
- Second rehearsal `30428876552` proved those two defects fixed: all release
  gates passed, along with macOS arm64, Windows x64 and every Linux x64
  package. macOS x64, Windows x86 and Linux arm64 then exposed one remaining
  contract: a target asset RID was not propagated through project references,
  so Core selected host binaries. The executable is now the sole asset-RID and
  package-content owner and directly includes the selected Core catalog files;
  no custom RID crosses a project-reference boundary. A clean Windows x86
  self-contained publish passes the common validator, and its aria2, FFmpeg
  and ffprobe SHA-256 values exactly match the x86 source assets. Full local
  validation passes with zero strict build warnings/errors, all 719 tests,
  format, module boundary, dependency, secret and diff gates. A project
  reference property-propagation attempt was rejected because a solution build
  created competing project instances that wrote the same `obj/bin` paths.
  PR quality `30430722500`, protobuf `30430722468` and CodeQL `30430722421`
  pass on `1968c9d`; each platform retains seven distinct TRX files, 718
  tests execute and pass, and only the runner-dependent FFmpeg seek test is
  not executed. Code and test annotations are zero; the one CodeQL annotation
  is GitHub's documented 300-file diff-display limit.
- Third rehearsal `30431043860` passes every release gate and all nine package
  jobs. Manual dispatch correctly skips release publication. All nine package
  sidecars match their package SHA-256; the nine manifests contain 54 valid
  required-file entries with version/RID agreement. Direct archive inspection
  passes for both Windows zips, both debs, the rpm, both AppImages and both
  DMGs, with required DownKyi/aria2/FFmpeg/ffprobe files and zero Config, Logs,
  Cache, Storage, Cookie, database or other user-data paths. Same-RID package
  manifests agree, and remote Windows asset hashes match the repository
  catalog.
- PR #112 is merged into `main` at `fcc1d226`; its tree exactly matches the
  tested integration head. Annotated tag `v1.1.0` points to that immutable
  commit.
- Tag workflow `30434000154` attempt 1 exposed a Windows lifecycle race before
  Desktop test discovery. The xUnit `-assemblyInfo` child printed valid JSON
  and then `Waiting 10 seconds for foreground threads to exit`, corrupting the
  runner protocol. Attempt 2 passed, but a successful rerun is not accepted as
  evidence that the foreground-thread owner has been fixed.
- The release produced by attempt 2 was immediately returned to draft. Package
  publication is frozen until the thread owner, test-host teardown and
  production Host shutdown path are verified and corrected.
- Local unmodified baselines currently pass 200 sequential and 400 concurrent
  `-assemblyInfo` process runs plus 50 full Desktop test runs. This establishes
  that the failure is intermittent; it does not close the blocker.
- The foreground output owner is now identified: the old test-data module
  initializer registered synchronous recursive cleanup on `ProcessExit`, and
  the xUnit v3 watchdog emitted its foreground-thread warning after valid
  `-assemblyInfo` JSON. Test isolation is now an async assembly fixture.
- Desktop tests use Avalonia's assembly-scoped headless xUnit session instead
  of a custom foreground Dispatcher thread. Production desktop shutdown awaits
  App, Host and runtime disposal; the production Host smoke also exposed and
  fixed a pooled SQLite connection surviving store disposal.
- The Assembly Lifecycle Stability Gate now probes load, assembly info,
  discovery, execution, assembly teardown and process exit in independent
  children. Its ownership audit covers production/test/benchmark/tool owners,
  and Windows delay forensics captures process/thread state and managed stacks.
- The historical schema 1 local Verification passed all seven test assemblies for five
  iterations: 211 phase results, zero failures, zero unknown owners, teardown
  at no more than 65 ms and process exit at no more than 170 ms on this Windows x64
  worktree. The report is deliberately marked dirty and is not release evidence.
- Review of that schema 1 report found that marker-aware execution was excluded
  from slow-phase capture and that process-exit used the collector observation
  time. Schema 2 captures every phase at the unchanged five-second threshold,
  fails unexplained missing evidence, separates execution/exit/timeout evidence
  and measures process exit from the fixture marker to OS `Process.ExitTime`.
- The first real execution stack identified a separate Windows delay:
  protocol-relative `//host/path` artwork was passed to `File.Exists` as a UNC
  path before HTTP classification. External image sources are now classified
  first; the focused image tests run in 172 ms and a subsequent full
  `DownKyi.Tests` execution completes in 1.893 seconds without crossing the
  slow threshold.
- Schema 2 formal local Verification now passes all seven assemblies for five
  iterations with 211 phase results, zero failures, zero unowned mechanisms,
  zero real slow phases and zero missing evidence. Teardown is at most 2 ms,
  OS-measured process exit is at most 15 ms, and `DownKyi.Tests` execution is
  1.685-1.716 seconds across the five runs. The 774.833 ms diagnostic capture
  time belongs to the deliberate marker-aware forensics self-test.
- The first main-profile 50-iteration run exposed a gate-side Windows sharing
  race at `DownKyi.Architecture.Tests` iteration 42: the fixture was appending
  its marker while `ReadAllLines` requested an incompatible handle. The marker
  reader now uses shared access, bounded retries and contention counters; a
  deterministic exclusive-lock self-test guards this path without weakening
  the final-marker contract.
- The fail-closed marker proof is now a detailed schema object rather than only
  a green summary bit. A Windows Main-profile five-iteration verification
  passed 212 phases and all 729 tests: the self-test executed, observed two
  contentions, recovered, parsed all marker states and reported no error. One
  5220.745 ms Architecture execution crossed the unchanged five-second
  threshold and retained evidence; no slow evidence was missing.
- Follow-up review made the proof predicate explicit rather than relying on
  derived booleans: positive contention and null error are checked directly,
  the self-test phase shares the final predicate, and mutation cases reject
  error-bearing, zero-contention and incomplete nominally-passed states.
  Unauthorized access and non-sharing I/O now remain marker read errors instead
  of being mislabeled as contention.
- The final consistency review removed the duplicate formal predicate: marker
  phase, summary and gate now consume one complete proof result. Phase schema
  now has general `failureType` / `errorType`, so marker contract failures no
  longer masquerade as slow-evidence capture errors.
- PR #116 is merged into `main` at `6a61247`. Strict PR CI
  `30450175286` and CodeQL `30450175415` passed. The authoritative Main
  lifecycle report ran all seven assemblies for 50 iterations: 2,102 phase
  results, zero failures, zero missing slow evidence, zero marker read errors,
  teardown at no more than 7 ms and OS process exit at no more than 187 ms.
  All 14 slow execution phases retained diagnostics. The marker self-test
  executed, observed two lock contentions, recovered, parsed the marker and
  passed every mutation check.
- `v1.1.0` remains an immutable audit tag; its GitHub Release draft is
  withdrawn. The corrective candidate uses the sole version source `1.1.1`,
  adds an exact tag/version contract and will publish only after a same-commit
  Main 50 run plus the complete 100-iteration cross-platform rehearsal.
- The first v1.1.1 local Verification exposed a gate-side threshold race:
  Architecture execution exited normally at 5005.587 ms, but the 25 ms
  monitor moved from below five seconds to an exited child and had no process
  left to inspect. The five-second classification is unchanged; forensics is
  now armed 100 ms early and the report discloses pre-threshold capture.
- The corrected final local Verification passes all seven assemblies for five
  iterations: 212 phase results, zero failures, five slow phases with five
  evidence captures, and zero missing evidence. The deterministic capture-lead
  self-test is true, the marker self-test observed two contentions with no
  error, teardown is at most 2 ms and OS process exit is at most 145 ms. This
  dirty-worktree report validates the candidate locally but is not release
  evidence.
- PR #117 merged the v1.1.1 corrective candidate into `main` at `c251a4f`.
  Strict PR CI `30453512364`, CodeQL `30453517140` and the exact-commit Main
  lifecycle run `30453933568` passed. The Main report contains 2,102 phases,
  zero failures, four captured slow phases, zero missing evidence, teardown at
  no more than 3 ms and OS process exit at no more than 24 ms.
- The first v1.1.1 Rehearsal `30455540672` correctly stopped before packaging.
  `DownKyi.Tests` assembly-info iteration 78 exited zero with valid xUnit JSON
  and empty stderr, but the post-exit scan observed one residual child. Schema 2
  stored only the count, so the hosted runner's exact child identity is
  unrecoverable. No test, Host, Dispatcher or application service executes in
  this phase.
- The same assembly-info command completed 500 isolated local repetitions with
  zero residual children. This excludes a deterministic application owner but
  does not erase the low-probability runner race. The corrective gate now
  preserves sanitized child identity and a residual evidence manifest, captures
  live managed children, and dynamically proves observation, classification and
  PID-plus-creation-time cleanup with a synthetic residual process tree.
- The residual-evidence candidate passes strict `AnalysisMode=All` build with
  zero warnings/errors and all 731 tests. Formal local lifecycle Verification
  covers 213 phases with zero failures, zero real residual children, five slow
  phases with five captures and zero missing evidence. Residual-child,
  redaction, capture-lead and marker-reader self-tests all pass; teardown is at
  most 3 ms and OS process exit at most 14 ms. Ownership now includes external
  process creation with 452 matches and zero violations. Format, module
  boundaries, vulnerable/deprecated dependency audits, Gitleaks and diff checks
  pass. This dirty-worktree result is candidate evidence, not Main or release
  evidence.
- PR #118 merged the residual-child evidence fix at `ad5ac64`. Strict PR CI
  `30461103180` and CodeQL `30461103401` passed; its PR lifecycle report has
  129 phases, zero failures, complete residual identity/evidence/classification/
  cleanup/redaction proof and zero real residual children.
- The exact-commit Main 50 run `30461640781` failed closed on one different
  boundary race: `DownKyi.Tests` execution iteration 24 ended normally at
  5007.386 ms, but the 100 ms pre-threshold arm was still skipped under runner
  scheduling. The report contains 2,103 phases, one `SlowEvidenceMissing`,
  zero real residual processes, and passing residual/marker self-tests. The
  five-second classification stays unchanged; capture now arms 1,000 ms early.
  Its held-child test uses a 1.25-second synthetic threshold, proving the full
  lead dynamically instead of passing through a zero-clamped arm.
- Clean code commit `ca8103e` passes strict `AnalysisMode=All` build with zero
  warnings/errors, all 731 tests, format, module boundaries,
  vulnerable/deprecated dependency audits, Gitleaks and diff checks. Its
  five-iteration lifecycle run covers seven assemblies and 213 phases with
  zero failures, seven slow phases, seven evidence captures, zero missing
  evidence and zero real residual processes. Both dynamic self-tests pass;
  teardown is at most 7 ms and OS process exit is at most 75 ms. Diagnostic
  collection consumed 19,765.952 ms in total and remains separately disclosed;
  exact-commit PR and Main evidence are still required.

## Gate 10 Checklist

- [x] Create the integration branch from latest `origin/main`.
- [x] Merge the complete release-hardening stack without conflicts.
- [x] Pass strict build and all tests before changing the version.
- [x] Verify version-derived assembly, file and informational metadata for
      `1.1.0`; UI and package validation remains part of the publish rehearsal.
- [x] Repeat the sanitized authenticated read-only Bilibili contract audit on
      the final candidate and run Gitleaks.
- [x] Pass strict PR CI and CodeQL on Windows, Linux and macOS for the final
      candidate SHA.
- [x] Manually dispatch `.github/workflows/build.yml` on that SHA.
- [x] Require Windows x64/x86, Linux x64/arm64 and macOS x64/arm64 package jobs
      plus their release-gate jobs to pass.
- [x] Inspect every package, `.sha256` sidecar and publish manifest; confirm
      DownKyi, aria2, FFmpeg, ffprobe, Fluent theme and version.
- [x] Confirm settings JSON, legacy SQLite, unfinished tasks, GID, partial file
      map, completed segment keys and resume fixtures remain green.
- [x] Confirm the source candidate and inspected packages contain no
      credential, account data, Config, Logs, Cache, Storage or user database.
- [x] Merge the integration PR into `main`.
- [x] Verify clean `main` points at the tested integration tree.
- [x] Create immutable annotated tag `v1.1.0` and run the tag workflow.
- [x] Identify the owner of the intermittent Windows foreground thread; record
      whether it belongs to xUnit discovery, the Avalonia test Dispatcher, a
      hosted service or production application lifecycle.
- [x] Replace the custom headless Avalonia foreground thread with the official
      assembly-scoped headless test session and deterministic async teardown.
- [x] Exercise production-style Host startup, shutdown and desktop-lifetime
      exit with bounded completion assertions.
- [x] Add the ownership audit, six-phase Assembly Lifecycle Stability Gate,
      automatic Windows timeout forensics and blocking PR/main/rehearsal jobs.
- [x] Pass the formal local five-iteration lifecycle Verification with managed
      stack forensics validated and retain its machine-readable report.
- [x] Correct schema 1 timing/evidence ambiguities and prove marker-aware
      execution captures a managed stack at the unchanged slow threshold.
- [x] Remove protocol-relative image UNC probing and add a bounded regression
      test so `//host/path` reaches HTTPS without blocking local file lookup.
- [x] Make lifecycle marker sampling tolerate concurrent fixture writes and add
      an exclusive-lock recovery self-test.
- [x] Repeat Windows assembly-info and Desktop/full-solution tests enough to
      make the original race observable if it remains.
- [x] Pass a new strict PR quality run on the corrected lifecycle commit.
- [x] Merge v1.1.1 PR #117 and pass its exact-commit Main 50 lifecycle profile.
- [x] Preserve the failed Rehearsal evidence and classify the remaining gap as
      missing residual-child identity, not a passing rerun.
- [x] Repeat the exact failing assembly-info command 500 times without observing
      a deterministic residual owner.
- [x] Merge the residual-child identity, evidence and dynamic self-test fix.
- [x] Preserve the failed post-merge Main report showing the 100 ms arm window
      is insufficient; do not replace it with a blind rerun.
- [ ] Merge the one-second pre-threshold capture-arm correction.
- [ ] Pass a new exact-commit Main 50 lifecycle profile after that fix.
- [ ] Pass the complete manual release rehearsal on the final versioned
      candidate commit.
- [ ] Keep `v1.1.0` immutable. Document the withdrawn draft and publish a
      `v1.1.1` corrective version only after every lifecycle and release gate
      is green.

## Stable Contracts

- Do not change settings JSON names, SQLite schema semantics, task IDs, GIDs,
  partial-file maps or resume identity while preparing the release.
- Do not reintroduce Prism, DryIoc, EventAggregator, RegionManager,
  ContainerLocator, string ViewName navigation, static HTTP state or another
  logging sink.
- `version.txt` remains the only version source; project files cannot define a
  second `Version`, `VersionPrefix`, `AssemblyVersion`, `FileVersion` or
  `InformationalVersion`.
- Manual workflow dispatch may build artifacts but cannot publish a GitHub
  Release. Only an exact `v1.1.1` tag matching the tested `version.txt` may
  publish; `v1.1.0` cannot be moved, updated or republished.
- Release artifacts must not contain developer credentials or application
  data.

## Verification

Run sequentially in one worktree:

```powershell
dotnet restore ./DownKyi.sln
pwsh ./script/validate-release-version.ps1
dotnet build ./DownKyi.sln -c Release --no-restore --no-incremental `
  -p:EnableNETAnalyzers=true -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true
pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/audit-lifecycle-ownership.ps1 `
  -OutputDirectory ./artifacts/assembly-lifecycle/ownership
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Iterations 5 `
  -NoBuild `
  -ValidateForensics `
  -ResultsDirectory ./artifacts/assembly-lifecycle/verification
dotnet format ./DownKyi.sln --verify-no-changes --no-restore
pwsh ./script/audit-module-boundaries.ps1
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
git diff --check
```

## Completion

Gate 10 is complete only when the Windows foreground-thread owner is identified,
the relevant test and production teardown paths are deterministic, the formal
five-iteration Verification report passes, the release `Rehearsal` profile
runs at least 50 iterations per assembly with diagnostics enabled, a complete
rehearsal passes on the corrected commit, and the corrective GitHub Release is
published with validated artifacts and notes. A successful blind rerun is not
completion evidence.

## Rollback

- Before merge: close the integration PR and leave `main` unchanged.
- After merge but before tag: revert the merge commit; do not create the tag.
- After publication: never move or reuse `v1.1.0`. Withdraw invalid artifacts,
  document the reason and publish the next corrective version. Never repair a
  release by moving an existing tag.
