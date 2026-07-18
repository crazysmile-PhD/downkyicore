# DownKyi Core Live Refactoring Plan

Status: awaiting cross-platform package CI
Last updated: 2026-07-18
Current group: PR 30-32
Current branch: `refactor/pr-30-32-release-hardening`

This file contains only unfinished work. Completed items are removed in the same PR that finishes them; newly discovered debt is added immediately with an owning PR or phase.

## Branch And Pull Request Policy

- PR 02 uses only `refactor/pr-02-host-composition` and one Pull Request.
- PR 03-06 uses only `refactor/pr-03-06-download-domain-store` and one Pull Request.
- PR 07-15 uses only `refactor/pr-07-15-download-runtime` and one Pull Request.
- PR 16-24 uses only `refactor/pr-16-24-media-ui-lifecycle` and one Pull Request.
- PR 25-29 uses only `refactor/pr-25-29-remove-legacy` and one Pull Request.
- PR 30-32 uses only `refactor/pr-30-32-release-hardening` and one Pull Request.
- A group may contain multiple ordered commits, but it must not be split into smaller public PRs or combined with another numbered range.
- The next group starts only after the previous group has completed its full scope and passed build, tests, data compatibility checks, documentation updates, and `git diff --check`.

## Active: PR 30-32 - Release Validation

Branch: `refactor/pr-30-32-release-hardening`

- Push this branch, run the manually dispatchable `Build` workflow, and require every Windows, Linux, and macOS release-gate/package job to pass.
- After the cross-platform run passes, remove this final item, mark the plan complete, and keep future debt as newly owned work rather than restoring completed PR 30-32 entries.

## Execution Rules

- Build and test sequentially; parallel build/test can contend for the same PDB and create a false local failure.
- Every PR must build, test, run, preserve user data, update this plan, update the AI knowledge graph, and pass `git diff --check`.
- PR CI blocks definite failures; benchmarks and noisy system profiling report regressions until stable thresholds exist.
