# PR #197 Stage 5 Rehearsal Capacity / Parallelization Checkpoint

Status: local implementation and validation complete. Push, natural required
CI and same-head review are deliberately deferred until later review
remediation forms one unified exact head. This is an independent scheduling
and capacity checkpoint. It is not Stage 5 lifecycle remediation, does not
close Stage 5, and does not start Stage 6.

Starting authority:
`6f700058d44e1bf8f2e3a8ca7c53dce598054a9d` on
`fix/pr196-review-followup`.

## Objective

Make the existing 100-iteration release Rehearsal finish within its unchanged
180-minute job budget by assigning each exact Windows-owned test assembly to
an independent required matrix cell. The checkpoint changes scheduling only.

It must not change:

- `Rehearsal = 100` or the six lifecycle phases;
- `PhaseTimeoutSeconds`, `TransitionBudget`, cancellation, timeout or cleanup
  semantics;
- process, marker, report or artifact ownership inside the lifecycle runner;
- slow thresholds, correctness oracles, retry behavior or sleeps.

## Measured Capacity

The exact starting-head PR report ran three iterations per assembly. Its
assembly phase consumed 444.861 seconds, or 148.287 seconds for one complete
serial round. Execution consumed 94.7% of that round; repeated load,
assembly-info and discovery startup consumed 5.2%; teardown and exit consumed
0.2%. The two slowest assemblies, `DownKyi.Windows.Tests` and
`DownKyi.Architecture.Tests`, accounted for 82.5% of assembly wall time.

Projecting the measured round to 100 iterations gives 247.15 minutes for the
assembly phase. Including setup and fixed validation work gives approximately
251 minutes. A prior partially completed 100-round Rehearsal underpredicted by
4.8%, so the reasonable serial range is 251 to 264 minutes. The unchanged
180-minute job budget is therefore short by approximately 71 to 84 minutes.

The measurements identify expected test execution as the capacity root cause,
not diagnostic collection, reporting, a lifecycle anomaly or a recent broad
performance regression. No lifecycle timeout or oracle change is justified.

## Scheduling Contract

The release workflow matrix contains exactly these eight assembly owners:

1. `DownKyi.Application.Tests`
2. `DownKyi.Architecture.Tests`
3. `DownKyi.Core.Tests`
4. `DownKyi.Desktop.Tests`
5. `DownKyi.Domain.Tests`
6. `DownKyi.Infrastructure.Tests`
7. `DownKyi.Tests`
8. `DownKyi.Windows.Tests`

Each cell runs `Profile Rehearsal`, `-ValidateForensics` and an exact
`-AssemblyPattern`. It keeps the existing 180-minute outer job budget and runs
all 100 iterations for that assembly. `fail-fast: false` retains sibling
evidence after a failure; it does not make any failure optional. The aggregate
matrix job remains a required dependency of downstream release work.

Each cell owns an assembly-qualified results directory and artifact name. No
cell writes to or uploads another cell's output path.

## Executable Proof

- Structured workflow tests derive the exact assembly matrix, full Rehearsal
  invocation, unchanged timeout, unique artifact mapping and downstream
  all-shard success gate from parsed YAML.
- A mutation adds failure masking to the matrix job. The workflow architecture
  proof must fail exactly one test and is part of the review invariant corpus.
- Focused lifecycle smoke uses a smaller explicit iteration override only for
  local structural validation. It must produce one successful report for every
  exact matrix assembly with all forensics self-tests and lifecycle phases.
- Natural required CI is the authoritative 100-round proof. Every matrix cell
  must report 100 iterations, one exact assembly, no failures and a successful
  ownership/self-test contract at the same commit reviewed by Codex.

## Completion Conditions

1. Focused workflow Architecture tests and the scheduling mutation pass.
2. Full Architecture and review invariant corpus pass without changing their
   lifecycle or mutation expectations.
3. Smaller-iteration per-assembly structural smoke proves report isolation and
   exact assembly selection.
4. Workflow syntax validation, formatting and `git diff --check` pass.
5. The checkpoint is committed independently and pushed normally.
6. Natural required CI completes every 100-round shard on the exact head.
7. Same-head Codex review is clean before this checkpoint can be called closed.

## Local Validation Record

The independent checkpoint completed these local gates:

- workflow ownership Architecture tests: 21 passed, zero failed;
- deliberate failure-masking mutation: 21 executed, 20 passed and exactly the
  expected shard mutation failed with exit code 1;
- full Architecture: 382 passed, zero failed;
- review invariant corpus: 13 invariants, seven test projects, 410 normal
  tests and 33 adversarial profiles passed;
- strict Debug and Release solution builds: zero warnings and zero errors;
- parallel structural Rehearsal: eight exact assembly cells, one iteration per
  cell, eight successful isolated reports, six target phases plus three gate
  phases per report and zero failed phases;
- every structural report passed ownership, marker-reader, process-lease,
  slow-ordering, reporter and collector self-test contracts;
- `actionlint` 1.7.12 accepted all 11 workflows;
- `dotnet format --verify-no-changes` and `git diff --check` passed.

The first local Architecture attempts encountered six stale MSBuild
`/nodeReuse:true` servers created by an earlier strict build. The standalone
recovery provider still derived its 57-input closure in 26.449 seconds. After
normal `dotnet build-server shutdown` and process-local
`MSBUILDDISABLENODEREUSE=1`, the recovery policy class passed 15/15 and full
Architecture passed 382/382. No repository timeout, ownership or lifecycle
policy changed.

The authoritative 100-round proof has not run on this intermediate head, as
requested. No push, required CI dispatch or Codex review belongs to this local
checkpoint handoff.

## Rollback

Revert the checkpoint commit. This restores the prior serial workflow and its
single artifact owner without changing the lifecycle runner, its test policy or
any process-supervision behavior. Because the measured serial duration exceeds
the job budget, rollback also restores the known capacity failure.
