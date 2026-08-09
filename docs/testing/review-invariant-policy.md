# Review Invariant Policy

## Purpose

Reviewer and Codex findings are not complete when only the immediate production
defect is fixed. A finding with a reusable root cause must also become a
permanent, executable invariant so later refactors cannot recreate it under a
different class, receiver, backend or platform.

## Required Workflow

Every actionable review finding starts a root-cause investigation. Complete
these steps before modifying production code:

1. **Identify the violated invariant.** Name the system contract, protocol,
   ownership rule, state transition or lifecycle invariant. A source line is
   evidence, not the invariant.
2. **Trace the complete failure path.** Follow the input or external event into
   result classification, retry, cleanup, persistence, physical files,
   finalization and the observable UI outcome. Locate the earliest boundary
   that lost required information or made the wrong transition.
3. **Search sibling paths.** Inspect every caller of the same result, helper,
   owner, exception mapper, sentinel, cleanup path, retry path and state
   mutation. A failure class must be closed across all its entry points.
4. **Classify local versus systemic.** A local patch is permitted only with
   evidence that no sibling path shares the defect. Weak result models,
   incomplete failure taxonomy, ambiguous sentinels, non-atomic transitions,
   duplicate owners and missing commit boundaries require a fix in the shared
   abstraction or true owner.
5. **Preserve information.** Do not compress failure kinds needed for retry,
   cleanup, persistence or user outcome into `bool`, `null`, an empty
   collection, generic exception, exit code or free-form text. Use a typed
   result, enum or discriminated state, and never make callers infer semantics
   from side effects.
6. **Delay irreversible side effects.** Source deletion, resume-identity
   removal, completed-key invalidation, destination overwrite and completed
   publication occur only after the final required validation. Workflows that
   cross filesystem, backend and durable Domain state need an explicit success
   order and failure recovery; a single owner does not make them atomic.

Before adding code, search current production modules and tests. Reuse or extend
the existing owner; never create a parallel registry, limiter, validator, retry
policy or lifecycle owner. Fix production behavior only when the defect still
exists on the current base. A historical finding already covered by current
code is linked to representative tests instead of reimplemented.

## Prohibited Remediation

Do not close a finding with any of these patterns unless evidence proves it is
the complete root cause:

- add one condition to the reported `if` or catch only the reported exception;
- introduce another sentinel such as `null`, empty collection, exit code or
  `File.Exists == false` to guess protocol or failure semantics;
- encode current implementation behavior as an architecture contract merely to
  make a regression green;
- add only the reported example without inspecting its failure family;
- revoke durable state after cleanup failed, or perform destructive cleanup
  before the operation commit boundary;
- treat green CI or existing architecture tests as proof that the semantic
  contract is correct.

## Failure And Transition Matrix

Classification, retry, cleanup, lifecycle, persistence and state-machine fixes
must create or update a matrix derived from the invariant. At minimum classify
the applicable rows separately:

- invalid or corrupt input;
- missing input;
- inaccessible input or destination;
- dependency/runtime/process failure;
- timeout or transport failure;
- destination conflict;
- cleanup failure;
- caller cancellation, pause and shutdown.

For every row, define expected physical source, partial/sidecar, completed key,
backend/resume identity, destination output, retry eligibility and durable task
state. A new failure kind must fit this matrix; it cannot fall into a generic
fallback that callers reinterpret.

Tests are derived from this invariant or the external protocol contract, not
from the implementation just written. Prefer behavior, failure injection and
transition assertions over source-text details. Findings with the same root
cause share one invariant even when they came from different PR comments.

## External Protocol Evidence

For Bilibili API, protobuf, HTTP, FFmpeg, aria2, SQLite and other external
contracts, one successful or failed fixture is insufficient. Termination,
absence, empty result and failure remain distinct. Before promoting an
assumption into architecture documentation or a regression, confirm it against
the repository protocol definition, official behavior, a real sanitized fixture
or another primary source. Insufficient evidence stays marked as an assumption
or unresolved contract.

## Repeated-Review Escalation

If a later review round on the same PR exposes the same failure family, the
previous remediation did not close the root cause. Stop local patches. Reopen
the shared abstraction, typed result, state machine, commit boundary, ownership
or transaction analysis and replace the patch chain with one systemic invariant
and failure/transition matrix.

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

A finding is complete only when the violated invariant and earliest incorrect
boundary are documented, sibling paths are searched, the fix lives in the
smallest correct shared owner, the invariant-derived regression or matrix is
green, and architecture/knowledge/plan documents match executable behavior. No
new undocumented sentinel, failure interpretation or destructive lifecycle rule
may remain. The representative invariant, relevant CI tier and this corpus must
agree; heavy evidence remains machine-readable and cannot be replaced by a
single green rerun.
