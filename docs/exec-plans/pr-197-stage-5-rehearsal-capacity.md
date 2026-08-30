# PR #197 Stage 5 Rehearsal Capacity / Parallelization Checkpoint

Status: superseded scheduling checkpoint plus active release-ready topology
implementation. The original exact-assembly matrix proved the serial capacity
problem but still assigned all 100 iterations to one runner. The active change
preserves the same proof across deterministic shards and adds a cheap PR
preflight. Hosted after measurements, push and same-head review are pending.
This remains Assembly lifecycle CI performance work; it does not start Stage 6.

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

## 2026-08-30 Exact-Assembly Performance Audit

This audit tested whether the two slow Windows release cells could reach ten
minutes without changing `Rehearsal = 100`, `-ValidateForensics`, slow
thresholds, deadlines, ownership, tree quiescence, cleanup or blocking tests.
It made no performance implementation change because the measured irreducible
execution workload already exceeds that target by more than an order of
magnitude.

Build run `33299979981` sampled PR head
`aad412f80a87a880c16c6ec7dfb24148255b62db`. Both cells were externally
cancelled after their repeated-gate steps had run for 66 minutes and one
second; their `always()` artifact uploads succeeded. The partial artifacts
contained 83 complete Architecture executions and 49 complete Windows
executions:

- Architecture job wall time was 69:42 before cancellation: restore 1:06,
  strict Release build 2:09 and repeated gate 66:01.
- Windows job wall time was 69:11 before cancellation: restore 0:53, strict
  Release build 1:50 and repeated gate 66:01.

| Assembly | Complete hosted samples | execution average | p50 | p95 | max |
| --- | ---: | ---: | ---: | ---: | ---: |
| `DownKyi.Architecture.Tests` | 83 | 45.186 s | 44.051 s | 49.588 s | 57.743 s |
| `DownKyi.Windows.Tests` | 49 | 77.804 s | 77.457 s | 79.497 s | 80.787 s |

Architecture's recovery-closure test was the largest single hosted test, but
not the whole workload. It averaged 6.019 seconds (p95 7.173 seconds, maximum
9.718 seconds) across all 83 completed executions. Each fresh xUnit process
re-created the test class's process-local `Lazy` value, so the closure was
indeed derived once per Rehearsal iteration. A separate local provider profile
with `MSBUILDDISABLENODEREUSE=1` evaluated seven projects and returned 60
inputs in 10.980, 8.927 and 9.936 seconds. Hoisting a fully keyed, verified
closure derivation once per job is the next safe removable repeated cost, but
the hosted execution minimum would still be approximately 65 minutes for 100
rounds before load, discovery, assembly-info, fixed forensics, restore or build.
It therefore cannot make the cell a ten-minute job.

Windows was not a PowerShell-cold-start problem. Across 49 hosted executions,
the two `AriaServerWindowsTests` together averaged 0.052 seconds per iteration.
The slow work was the linked Windows process-supervision corpus: the five-second
failed-publication proof, four-second caller-window mutation, several
three-to-three-and-a-half-second timeout/reap/drain/ownership mutations, and
multiple two-second tree, inherited-handle and observer proofs. Those durations
are the fail-closed semantics under test and cannot be cached or shortened
without weakening proof.

The ahead-two local checkout at `c6277c9aa1a383d8ddd6c9f07d0b234e6e7d2469`
also contained pre-existing unstaged lifecycle WIP, so it was measured only as
an advisory prospective working state, not an exact comparative baseline or
PR-head CI. Additional unrelated WIP appeared in the shared checkout during
the audit and was preserved. Three complete
`-ValidateForensics` iterations passed with zero residual children and zero
missing slow evidence. Architecture averaged 131.347 seconds per complete
iteration and projects to 219.77 minutes for the repeated gate; Windows
averaged 89.455 seconds and projects to 149.89 minutes. The fixed gate work was
51.483 and 47.696 seconds respectively. Both measurements retained load,
assembly-info, discovery, execution, teardown, exit, ownership and forensics.

No after benchmark existed for that one-runner-per-assembly audit because no
proof-equivalent implementation could make one runner meet the requested
target. Replacing the Windows
PowerShell fixtures would save only seconds across 100 rounds, while the
existing ready/hold ProcessSupervision fixture was slower in a 15-round
start/ready/kill/reap microprofile. The safe stop condition for single-runner
optimization was therefore the full repeated test workload itself, not a
lifecycle anomaly, process leak, collector failure or slow-threshold
classification.

## 2026-08-30 Release-Ready Sharded Topology

This section supersedes the earlier one-runner-per-assembly scheduling
contract. It does not supersede the measurements: those measurements are the
reason work distribution, rather than a weaker oracle or a faster fake child,
is required.

The implementation uses
`docs/testing/assembly-lifecycle-release-topology.json` as the single scheduling
authority. Ordinary pull-request Build runs execute a one-iteration lock
preflight for Architecture and Windows with strict Release selected-closure
builds and the complete forensics contract. The 100-round proof starts only
when the pull request has the `assembly-lifecycle-release-ready` label, or for
the existing non-PR release event.

The heavy proof has two waves and never requests more than 20 standard hosted
runners from this workflow at once:

1. Six standard assembly owners run with `max-parallel: 4`, while Architecture
   runs 16 shards. The first four Architecture shards own seven iterations and
   the other twelve own six, totalling exactly 100.
2. After all standard owners and the Architecture aggregate succeed, Windows
   runs 20 shards of five iterations, again totalling exactly 100.

Every shard retains `Profile Rehearsal`, `-ValidateForensics`, the exact
assembly pattern, all six lifecycle phases and the original runner policy. The
selected-closure build is a setup optimization borrowed selectively from
`perf/ci-pr-five-minute-topology`; it builds the probe, central runner and exact
test-project closure with node reuse and shared compilation disabled. It does
not cache `bin`/`obj`, reuse another job's output or hoist runtime proof out of
the loop.

The exact-SHA aggregate rejects missing, duplicate or stale shards, wrong
iteration allocations, copied or modified reports, incomplete forensics,
residual processes and failed phases. Its deliberate missing-shard,
duplicate-shard, stale-commit and wrong-report-hash mutations must all fail
closed before aggregate evidence is accepted.

### Before and projected after

The cancelled hosted baseline implies approximately 79:33 for the Architecture
100-round repeated gate (`66:01 / 83 * 100`) and 134:44 for Windows
(`66:01 / 49 * 100`), before their restore and strict-build setup. That is
roughly 83 minutes and 138 minutes of job wall clock respectively.

With unchanged per-iteration work, the slowest Architecture shard owns seven
iterations and the Windows shards own five. The projected repeated portions
are therefore about 5:34 and 6:44. Adding isolated selected-closure setup gives
an expected per-shard wall clock near 9 to 10 minutes, with 12 minutes retained
as the hosted-runner p95 acceptance bound. Because Windows intentionally waits
for wave 1, end-to-end heavy critical path is expected near 19 to 21 minutes,
not five minutes. These are projections, not after measurements; only a
label-enabled exact-head hosted run can supply the required after p50, p95 and
slowest-shard evidence.
