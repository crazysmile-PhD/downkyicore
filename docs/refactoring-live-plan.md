# DownKyi Release And Verification Policy

This document owns stable release, verification, completion and rollback
policy. It is not a current-work database. Do not record an active item, next
item, branch, commit SHA, CI state, progress checklist or completed history
here.

Owner-requested work that may survive the current Codex context is bookmarked
in GitHub Issue [#137](https://github.com/crazysmile-PhD/downkyicore/issues/137).
Each bookmark points to its existing PR or task-specific detail source. Product
PRs do not update this file merely because their work state changed.

## Release Policy

- Preserve true semantic dependencies and recheck a downstream change after its
  prerequisite changes; stale exact-head evidence does not transfer to a new
  base. Separate root causes may remain separate commits or review evidence,
  but do not grow an unmerged release stack beyond roughly two or three layers
  or material divergence from `main`. Consolidate accepted semantics onto one
  clean current-main integration branch and validate that exact head.
- Publish only from one clean final commit after strict quality, CodeQL, Main
  lifecycle, release rehearsal and Windows/Linux/macOS package validation pass
  for that exact commit.
- Preserve settings JSON, legacy SQLite, unfinished tasks, GID, partial-file
  maps, completed keys and resume fixtures unless an approved migration with
  rollback evidence explicitly changes them.
- Source and packages must not contain Cookie values, account data, local
  Config/Logs/Cache/Storage or developer artifacts.
- Existing tags are immutable. Do not change `version.txt`, create a tag or
  publish a release while any release blocker or required gate is unresolved.

## Verification

Run sequentially in one worktree:

```powershell
dotnet restore ./DownKyi.sln
pwsh ./script/validate-release-version.ps1
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
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml | `
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
git diff --check
```

A result is valid only when its runtime, OS, architecture, exact commit and
dirty-worktree state are recorded. Cross-machine timings are not compared
directly.

## Completion And Rollback

Work is complete only after implementation, focused regressions, required
documentation, exact-head CI and review are green. Remove its bookmark from
#137; stable facts go to architecture, maintenance or release documentation.
Do not add a completed section to the workboard or this policy.

Before merge, rollback means closing the draft and deleting only the feature
branch. After merge, revert the complete change range without modifying user
data formats or reintroducing a security bypass. A migration requires its own
backup, rollback and reopen evidence.

Gate 10 is complete only when formal local lifecycle verification, the exact
Main profile and release rehearsal all remain green. A successful rerun does
not by itself prove that an intermittent lifecycle owner was fixed.
