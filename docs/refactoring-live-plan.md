# DownKyi Release And Verification Policy

This document owns stable release, verification, completion and rollback
policy. It is not a current-work database. Do not record an active item, next
item, branch, commit SHA, CI state, progress checklist or completed history
here.

Owner-requested work that may survive the current Codex context is managed only
in [DownKyiCore 工作項目](https://github.com/users/crazysmile-PhD/projects/2).
The Project owns Priority, Status, ordering, Draft items, items awaiting
verification or a decision, In Progress and Done. Each linked Issue owns one
work item's background, scope, evidence, acceptance criteria and PR links.
Product PRs do not update this file merely because their work state changed.

## Release Policy

- Preserve true semantic dependencies and recheck a downstream change after its
  prerequisite changes; stale exact-head evidence does not transfer to a new
  base. Separate root causes may remain separate commits or review evidence,
  but do not grow an unmerged release stack beyond roughly two or three layers
  or material divergence from `main`. Consolidate accepted semantics onto one
  clean current-main integration branch and validate that exact head.
- Publish only from one clean final commit after strict quality, CodeQL and
  Windows/Linux/macOS package validation pass for that exact commit.
- Preserve settings JSON, legacy SQLite, unfinished tasks, GID, partial-file
  maps, completed keys and resume fixtures unless an approved migration with
  rollback evidence explicitly changes them.
- Source and packages must not contain Cookie values, account data, local
  Config/Logs/Cache/Storage or developer artifacts.
- A public GitHub Release contains exactly the nine supported installers plus
  `SHA256SUMS.txt` and `release-manifest.json`. Per-package checksum sidecars and
  per-package publish manifests remain internal validation inputs; the release
  workflow must assemble, verify and publish the eleven-file public set through
  `script/assemble-release-assets.ps1`.
- Existing tags are immutable. Do not change `version.txt`, create a tag or
  publish a release while any release blocker or required gate is unresolved.

## Verification

The canonical commands, order and rollback procedure live in
`docs/operations/verification-and-rollback.md`. Run them sequentially in one
worktree; this policy intentionally does not duplicate the command list.

A result is valid only when its runtime, OS, architecture, exact commit and
dirty-worktree state are recorded. Cross-machine timings are not compared
directly.

## Completion And Rollback

Work is complete only after implementation, focused regressions, required
documentation, exact-head CI and review are green. Set the GitHub Project item
to Done and retain it there; stable facts go to architecture, maintenance or
release documentation. Do not add a parallel completed section to this policy
or another Issue.

Before merge, rollback means closing the draft and deleting only the feature
branch. After merge, revert the complete change range without modifying user
data formats or reintroducing a security bypass. A migration requires its own
backup, rollback and reopen evidence.
