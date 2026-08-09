# Root-Cause Review Policy Baseline

Status: local verification complete; exact-head CI pending

## Goal

Land the Root-Cause Review Remediation rule and its minimal executable invariant
gate directly on `main` before further #120 or #127 remediation.

## In Scope

- Agent and testing policy for invariant identification, full failure-path and
  sibling-path analysis, information-preserving typed results, commit boundaries
  and repeated-review escalation.
- A main-compatible machine-readable corpus that references only tests already
  present on `main`.
- A deterministic runner that proves every declared test class actually ran.
- Cross-platform PR CI invocation, architecture contract checks, knowledge graph
  and formal Verification commands.
- A scope-containment contract: analysis may find adjacent defects, but this PR
  changes only the policy baseline and its necessary executable gate.

## Out Of Scope

- SemVer, update-dialog or other product fixes found while constructing the
  corpus. These are the concrete #129 example of findings that require a
  separate product PR rather than scope expansion of the policy PR.
- Any #120 or #127 review remediation, thread resolution, GitHub comment or PR
  merge.
- Claims that #125 output reservation or #127 FFmpeg failure taxonomy already
  exist on `main`.

## Completion

1. Strict Release build and all seven test projects pass.
2. Review corpus executes every declared class on Windows, Linux and macOS.
3. Format, module, secret, workflow, package and lifecycle gates pass.
4. The policy PR is based directly on `main`, contains no product behavior, and
   exact-head GitHub checks are green.
5. Only after this baseline merges may work resume on #120 and then #127.

## Local Verification

Verified on Windows x64 against `origin/main` at `c4d00b3296922844826343e5ba82085364ebba32`:

- Strict Release build: 0 warnings, 0 errors.
- Review invariant corpus: 9 invariants, 7 projects, 239 tests passed.
- Full solution: 806 passed, 1 existing real-binary test skipped, 0 failed.
- Architecture gate: 229 passed, 0 failed.
- Assembly lifecycle: 7 assemblies, 213 phase results, 0 failures.
- Lifecycle ownership: 477 matches, 0 violations.
- Format, module boundary, workflow lint, package vulnerability/deprecation,
  Gitleaks and `git diff --check`: passed.

The final diff contains policy, documentation, CI workflow, test and script
changes only. It contains no application production-code changes.

## Rollback

Revert the policy commit range. No user settings, SQLite data, downloads,
resume state or external protocol behavior changes in this PR.
