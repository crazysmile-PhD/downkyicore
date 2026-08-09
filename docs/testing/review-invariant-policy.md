# Review Invariant Policy

## Purpose

A review finding is evidence of a symptom, not a complete patch specification.
Before production code changes, the agent must identify the violated invariant,
trace the whole failure path, search sibling entry points and locate the first
boundary that lost information or made an invalid state transition.

## Required Workflow

1. **Identify the violated invariant.** Name the system contract, protocol,
   ownership rule, state transition or lifecycle invariant. A source line is
   evidence, not the invariant.
2. **Trace the complete failure path.** Follow the input or external event into
   classification, retry, cleanup, persistence, physical files, finalization
   and the observable UI outcome. Locate the earliest incorrect boundary.
3. **Search sibling paths.** Inspect callers of the same result, helper, owner,
   exception mapper, sentinel, cleanup path, retry path and state mutation. A
   failure family must be closed across all entry points.
4. **Classify local versus systemic.** A local patch is permitted only with
   evidence that no sibling path shares the defect. Weak result models,
   incomplete failure taxonomy, ambiguous sentinels, non-atomic transitions,
   duplicate owners and missing commit boundaries require a shared-owner fix.
5. **Preserve information.** Failure kinds needed for retry, cleanup,
   persistence or user outcome cannot collapse into `bool`, `null`, an empty
   collection, generic exception, exit code or free-form text. Use typed results
   or discriminated state and do not infer semantics from side effects.
6. **Delay irreversible effects.** Source deletion, resume-identity removal,
   completed-key invalidation, destination overwrite and completed publication
   happen only after final required validation. Filesystem, backend and durable
   Domain state need an explicit commit order and failure recovery.

Before adding code, search current production modules and tests. Reuse or extend
the existing owner; never create a parallel registry, limiter, validator, retry
policy or lifecycle owner. Findings with one root cause share one invariant even
when they came from different review comments.

## Scope Containment

Investigation may widen the analysis surface, but it does not automatically
widen the current PR's modification scope. A sibling path belongs in the
current PR only when it shares the same root cause and is necessary to close the
same failure family.

A different invariant, product issue or incidental defect is recorded as a
finding and moved to the backlog or a separate PR. It must not be bundled merely
because it was discovered during the investigation. The root-cause requirement
expands evidence gathering; it does not authorize unrelated product changes.

## Prohibited Remediation

Do not close a finding by adding one condition to the reported `if`, catching
only the reported exception, inventing another sentinel, or encoding current
implementation behavior as an architecture contract. Do not add only the
reported example without searching its failure family. Green CI proves the
recorded checks passed; it does not prove an unexamined semantic contract.

Cleanup failure cannot be followed by durable invalidation, and destructive
effects cannot run before the workflow commit boundary.

## Failure And Transition Matrix

Classification, retry, cleanup, lifecycle, persistence and state-machine fixes
must create or update a matrix derived from the invariant. Applicable rows must
separate at least:

- invalid or corrupt input;
- missing input;
- inaccessible input or destination;
- dependency, runtime or process failure;
- timeout or transport failure;
- destination conflict;
- cleanup failure;
- caller cancellation, pause and shutdown.

For each row define the expected physical source, partial/sidecar, completed
key, backend/resume identity, destination output, retry eligibility and durable
task state. A new failure kind must fit the matrix instead of falling into a
generic fallback that callers reinterpret.

Tests derive from the invariant or external protocol, not from the
implementation just written. Deterministic failure injection, contract tests
and architecture self-tests run in PR CI. Repeated race, stress, GC, process,
real-binary and systematic platform checks stay in Main or release rehearsal
unless an existing security policy already requires PR coverage.

## External Protocol Evidence

For Bilibili API, protobuf, HTTP, FFmpeg, aria2, SQLite and other external
contracts, one successful or failed fixture is insufficient. Termination,
absence, empty result and failure remain distinct. Confirm assumptions against
the repository protocol definition, official behavior, a sanitized real fixture
or another primary source before promoting them to stable architecture. Mark
insufficient evidence as an assumption or unresolved contract.

## Repeated-Review Escalation

If a later review round on the same PR exposes the same failure family, the
previous remediation did not close the root cause. Stop local patches. Reopen
the shared abstraction, typed result, state machine, commit boundary, ownership
or transaction analysis and replace the patch chain with one systemic invariant
and failure/transition matrix.

## Executable Corpus

`review-invariant-corpus.json` maps root-cause invariants to representative test
classes already present on the target branch. It must not claim a contract that
exists only on an unmerged PR. `test-review-invariants.ps1` fails unless every
declared class actually executes and passes.

## Completion Rule

A finding is complete only when the violated invariant and earliest incorrect
boundary are documented, sibling paths are searched, the fix lives in the
smallest correct shared owner, the invariant-derived regression or matrix is
green, and architecture/knowledge/plan documents match executable behavior. No
new undocumented sentinel, failure interpretation or destructive lifecycle rule
may remain.
