# Review Invariant Policy

## Purpose

Reviewer and Codex findings are not complete when only the immediate production
defect is fixed. A finding with a reusable root cause must also become a
permanent, executable invariant so later refactors cannot recreate it under a
different class, receiver, backend or platform.

## Required Workflow

1. Search current production code and tests before adding anything. Reuse or
   extend the existing owner; never create a parallel registry, limiter,
   validator, retry policy or lifecycle owner.
2. Classify the finding by root cause. Findings with the same cause share one
   invariant even when they came from different PR comments.
3. Fix production behavior only when the defect still exists on the current
   base. A historical finding already covered by current code is linked to its
   representative tests instead of reimplemented.
4. Add a regression that fails on the defective behavior. Prefer behavior,
   failure injection and contract assertions over implementation text.
5. Put deterministic failure injection, contract tests and architecture
   self-tests in PR CI. Keep repeated race, stress, GC, process, real-binary and
   systematic platform evidence in Main or release rehearsal unless security
   policy already requires the PR matrix.
6. Test the gate itself. Static C# policy should use Roslyn when syntax or
   semantics matter, and every rule needs an adversarial fixture that proves a
   rename, modifier, file location or incomplete report cannot bypass it.

## Executable Corpus

`review-invariant-corpus.json` maps one root-cause invariant to representative
test classes. It does not replace focused tests and must not list every historical
comment separately. `ReviewInvariantCorpusTests` rejects missing projects,
missing classes, duplicate IDs, incomplete heavy-profile evidence and removal of
the seven-project coverage.

Run the deterministic PR corpus with:

```powershell
pwsh ./script/test-review-invariants.ps1 `
  -Configuration Release `
  -NoRestore `
  -NoBuild
```

The runner fails closed if an entry resolves to no test, a referenced project is
missing, a test fails or a declared test is not executed.

## Durable Output Ownership Example

Output reservation truth belongs to the existing Domain/SQLite store.
`DownloadTaskAdmissionService` remains the only admission coordinator and uses
transactional unique claims through the store. Tests may probe `(1)`/`(2)`
candidates, but admission must not scan all unfinished tasks and must not create
a mutable path registry or cache. Windows and macOS compare path claims without
case; Linux uses ordinal comparison. With automatic numbering disabled, a
conflict is rejected rather than silently renamed.

## Completion Rule

A general review finding is complete only when production behavior, the
representative invariant, the relevant CI tier and this corpus agree. Heavy
evidence remains machine-readable and cannot be replaced by a single green
rerun.
