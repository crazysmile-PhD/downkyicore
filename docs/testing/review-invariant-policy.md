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
7. **Derive the state space.** Before adding an example regression, determine
   whether the failure family can be expressed as properties, a deterministic
   generator or a state-transition model. The primary CI proof searches that
   state space; the reported input remains a counterexample, not the boundary
   of the contract.
8. **Prove the proof fails closed.** Important invariant oracles, architecture
   rules and static gates require an adversarial or mutation fixture. The
   fixture must intentionally remove an owner, skip a transition or violate the
   contract and demonstrate that the same proof rejects the mutation.

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

## State-Space Regression Rule

The required remediation flow is:

```text
review finding
-> root cause
-> invariant
-> sibling-path search
-> generator or state space
-> adversarial proof
-> production fix
```

It is not acceptable to substitute `finding -> one Assert -> one input fix`.
Use property tests when a property framework already exists, deterministic
generators when exhaustive bounded combinations are stable, and transition
models for ownership, retry and lifecycle state machines. A single named
regression may document the original counterexample, but it cannot be the only
guard when the same invariant spans multiple inputs or transitions.

The test oracle is part of the contract. Its adversarial fixture must exercise
the same decision logic used by the positive state-space proof. A check that
only searches source text for a required word, class name or method call does
not prove the behavior is fail closed.

## File Output Ownership

Every file created by an operation must satisfy this invariant when the
operation returns, fails or propagates cancellation:

```text
physical file does not exist
OR
the exact path, or its established sidecar base path, is present in durable task ownership state
```

The safe ordering is to claim the intended path through the existing durable
owner before the first filesystem write. If claim-first is not possible, the
operation must delete the file before returning and make cleanup failure
observable; a suppressed cleanup error cannot establish absence. Hard-coded
extension scans and mutable process-local path registries are discovery
fallbacks, not ownership truth. Multi-output operations require one stable key
per output and may not overwrite an earlier ownership entry with a sibling
file.

The PR gate for a file-producing failure family must use a deterministic
generator across relevant success, invalid-output, transport, I/O, permission
and cancellation outcomes. Its mutation fixture must remove at least one
durable owner from an otherwise valid generated state and prove the ownership
oracle reports the physical orphan.

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
declared class actually executes and passes. Important invariants declare proof
kinds separately from their current executable locator. An adversarial mutation
profile reruns the positive state-space proof with a deterministic defect and
requires a real failed test in TRX; an always-passing replacement therefore
fails the Gate. Test rename or decomposition is migrated by updating the proof
locator while preserving its required proof kinds and mutation outcome.

## Completion Rule

A finding is complete only when the violated invariant and earliest incorrect
boundary are documented, sibling paths are searched, the fix lives in the
smallest correct shared owner, the invariant-derived regression or matrix is
green, and architecture/knowledge/plan documents match executable behavior. No
new undocumented sentinel, failure interpretation or destructive lifecycle rule
may remain.
