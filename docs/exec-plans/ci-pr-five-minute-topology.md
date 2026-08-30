# CI PR Five-Minute Execution Topology

Status: independent checkpoint validated on a real draft PR; the latest
gate-authority head is blocked by an independent Stage 5 `SlowEvidenceMissing`
result, the five-minute performance acceptance is not met, and integration
remains pending Stage 5 closure

## Boundary

This checkpoint is independent of PR #197 Stage 5 closure. It started from
`db37fb1f094e575b5f750ca16614d6b3ec1b9653` on branch
`perf/ci-pr-five-minute-topology` in a separate worktree. It must not be merged
into, pushed to, or used to restate the status of `fix/pr196-review-followup`.
Stage 5 ownership, authorization, one-deadline, reap, quiescence, stream-drain,
TRX and fail-closed contracts remain inputs to this checkpoint.

The objective is to remove waiting and duplicate equivalent work, not evidence.
Windows, Linux and macOS project ownership, every required test project, the
review invariant corpus, every adversarial proof, the three-iteration PR
lifecycle profile, strict Release analysis and Windows Debug compilation remain
required.

On 2026-08-30 the independent Stage 5 branch advanced during this work from the
starting head to remote head `75bd15efb425ac9b652b1efdbe4b0952a73b62fc`.
Its separate worktree later contained 12 uncommitted Stage 5 files and then
committed them locally as `e512d08c2a56a57366ee881521669cfa3bc86f91`; that commit
was not pushed by this checkpoint. This work did not modify, discard or absorb
those changes. It overlaps the Stage 5 changes since the starting head in seven
files:
`docs/ai-knowledge-graph.md`, `docs/testing/README.md`,
`docs/testing/assembly-lifecycle-stability.md`,
`docs/testing/review-invariant-corpus.json`, `script/test-project-runner.ps1`,
`AssemblyLifecycleArchitectureTests.cs` and `CiTestActionBehaviorTests.cs`.
Integration therefore requires a post-closure rebase and full proof rerun, not
a direct merge. A read-only `git merge-tree --write-tree` check at Stage 5
`75bd15ef...` and this pre-measurement checkpoint completed without textual
conflicts; the six semantic overlaps still require explicit review.

Stage 5 also has an independent current blocker: Strict PR CI run `33289558395`
reported one `DownKyi.Core.Tests` iteration-2 execution failure on PR merge SHA
`a75838ece221bb63018916c7af9b42503b129851`. The target exited successfully at
5.257 seconds, but slow evidence returned `CallerCancelled` without starting
the collector, so schema 4 correctly recorded `SlowEvidenceMissing`. This
topology checkpoint does not absorb or repair that Stage 5 finding.

## Baseline

The baseline is the most recent commit with a complete successful five-workflow
PR bundle before this work started:
`6d4aae44bedf1d6dfadac43007dad480c2e9be5b`. The live GitHub run IDs are Strict
PR CI `33250687081`, Build `33250687137`, CodeQL `33250687074`, Process
Membership Feasibility `33250687055` and Protobuf `33250687114`.

Strict PR CI had one later rerun of the `osx-x64` aria2 cell. The table records
the successful job executions. The initial jobs provisioned in 2-5 seconds;
the rerun started 1,208 seconds after workflow creation. Therefore the stable
repository-controlled first-attempt critical path was the 668-second lifecycle
job, while the observed eventual all-green wall clock including the rerun queue
was 1,389 seconds. The rerun is diagnostic evidence, not a performance target.

| Workflow / required job | Wall clock | Major step durations |
| --- | ---: | --- |
| Strict / Format | 107 s | restore 13 s; format 87 s |
| Strict / Windows build and test | 583 s | restore 52 s; Release 124 s; Debug 126 s; Architecture 34 s; review 87 s; repository tests 129 s |
| Strict / Linux build and test | 361 s | restore 23 s; Release 98 s; Architecture 44 s; review 78 s; repository tests 108 s |
| Strict / macOS build and test | 278 s | restore 11 s; Release 57 s; Architecture 23 s; review 54 s; repository tests 116 s |
| Strict / Assembly lifecycle | **668 s** | diagnostics 16 s; restore 112 s; build 92 s; lifecycle 430 s |
| Strict / aria2 win-x86 | 161 s | restore 62 s; build 54 s; test 21 s |
| Strict / aria2 win-x64 | 163 s | restore 44 s; build 78 s; test 21 s |
| Strict / aria2 linux-x64 | 112 s | restore 14 s; build 73 s; test 20 s |
| Strict / aria2 linux-arm64 | 121 s | restore 13 s; system tool 13 s; build 65 s; test 21 s |
| Strict / aria2 osx-x64 | 181 s | restore 20 s; build 116 s; test 29 s |
| Strict / aria2 osx-arm64 | 81 s | restore 11 s; build 34 s; test 15 s |
| Strict / Package audit | 46 s | restore 11 s; vulnerable 20 s; deprecated 8 s |
| Build / FFmpeg tooling | 21 s | actionlint 9 s; asset guards 3 s |
| Build / manifest detection | 5 s | path detection 1 s |
| CodeQL / Analyze C# | 278 s | initialization 13 s; restore 13 s; build 182 s; analyze 62 s |
| Process membership / Linux | 6 s | checkout and executable probes 2 s |
| Process membership / macOS arm64 / x64 | 10 s / 27 s | native probe build 2 s / 8 s |
| Protobuf / generated code | 43 s | restore 14 s; build 22 s |

The baseline exposed four independent serial chains:

- Windows Release, Debug, Architecture, review corpus and repository tests ran
  one after another on one runner.
- `RunSolutionAsync` ran platform-owned projects sequentially.
- normal review classes repeated classes already covered by the equivalent
  same-OS, same-configuration repository TRX; all adversarial proofs then ran
  sequentially.
- lifecycle ran every assembly sequentially even though only iterations within
  one assembly require temporal ordering.

Restore and full-solution build were also repeated by Format, all three
repository lanes, lifecycle, package audit, CodeQL and every aria2 RID. The
topology may reduce a shard to its compiler-resolved project closure, but it
must not replace current-HEAD compilation with cached `bin` or `obj` outputs.

## Distributed Required Suite Contract

`docs/testing/ci-required-topology.json` is the machine-readable expected set.
All producer jobs begin without a serial `needs` predecessor. The final
`required-verdict` is the only join:

```text
Format -----------------------------+
Release (Windows/Linux/macOS) -------+
Debug (Windows) ---------------------+
Repository (2 shards x 3 OS) --------+--> exact-head semantic aggregator
Review mutations (4 shards x 3 OS) --+
Lifecycle (8 Windows assemblies) ----+
aria2 (6 RIDs) ----------------------+
Package audit -----------------------+
```

Repository shard membership is the deterministic modulo partition of the
existing compiler-discovered and platform-owned project order. Each shard
continues through the central structured runner. It builds only its selected
project closures with the same warnings, analyzers, code-analysis and code-style
settings, then permits at most two independently owned child test processes.
Builds remain sequential within one checkout so shared `bin`/`obj` writers do
not race; child tests have unique TRX paths and isolated process ownership.

Review proof identity is `invariant/environmentVariable`. Four deterministic
modulo shards run every proof on every supported OS. Mutation variables are an
immutable invocation option injected only into the one authorized child; the
PowerShell parent environment is never mutated. Normal review classes reuse the
repository TRX only when the aggregator independently proves the same exact
SHA, OS, Release configuration, project, central runner and class coverage.

Lifecycle shards are per exact assembly. Within one shard the PR profile still
runs iteration 1, then 2, then 3. Each shard builds the lifecycle probe, the
central runner (and its transitive process-supervision dependency) and the
selected test-project closure, not the whole solution. The aggregator requires
all eight Windows assemblies, every one of the six phases for every iteration,
and the three schema-v4 gate self-tests exactly once from the topology's named
`gateAuthorityAssembly`. Missing or duplicate assemblies, iterations, phases or
gate results fail closed; a non-authority assembly that supplies gate results is
also rejected. Rehearsal remains 100 sequential iterations per assembly on tag
or manual release execution; it is not ordinary PR work.

## Evidence Authority

Each build, repository and review producer checks that `git rev-parse HEAD`
equals the workflow SHA and that the worktree is clean before atomically
writing structured evidence. Lifecycle schema 4 already records commit SHA,
dirty state, ownership audit and phase results. NuGet cache contains only
`~/.nuget/packages`; its key covers `global.json`, `Directory.Packages.props`,
`Directory.Build.props`, `Directory.Build.targets`, every project and lock
file. Compiled outputs are never cached as correctness evidence.

The compiled aggregator derives the expected projects and proofs from current
repository policy rather than trusting artifacts. It rejects failed/skipped
upstreams, absent artifacts, malformed JSON, unknown or duplicate identities,
wrong SHA, zero tests, failed normal tests, mutation exit zero, wrong mutation
failure cardinality, missing TRX, incomplete lifecycle iterations and any TRX
that fails the existing semantic validator. Its adversarial corpus covers the
eight required distributed-topology mutations plus a missing lifecycle
iteration.

## Validation Record

Local evidence established the implementation before the remote measurement:

- strict Architecture project build: zero warnings and zero errors;
- full Architecture suite through the central owner: 399 executed, zero
  failures after contract migration;
- Windows repository suite: all 8 platform-owned projects green;
- review invariant gate: 14 invariants, 7 projects, 424 normal tests and all
  42 adversarial proofs green;
- one-assembly native lifecycle smoke: 674 ownership matches, zero violations,
  one assembly, nine schema-v4 results and zero failures;
- strict Release solution build: zero warnings/errors in 81.33 seconds after
  restore;
- Windows Debug solution compile: zero warnings/errors in 71.08 seconds;
- actionlint 1.7.12: every workflow file passed;
- `dotnet format --verify-no-changes`: 0 of 1,011 files required changes;
- `git diff --check`: clean apart from line-ending conversion notices;
- unique gate-authority contract tests: 56 executed across four classes, zero
  failures, including missing and unauthorized duplicate gate evidence;
- non-authority Domain lifecycle: three PR iterations, 18 assembly phases,
  zero gate results and zero failures; authority smoke: one iteration, all six
  assembly phases, all three gate results and zero failures.

Draft PR #203 then exercised the topology against GitHub's generated PR merge
SHA. Early natural failures exposed and fixed only topology defects: actionlint
shell quoting, missing lifecycle build-closure projects, missing fresh-shard
central-runner bootstrap, retained compiler/build-server processes and one
action behavior fixture that consumed an ambient outer shard. Superseded runs
also proved that the final verdict skips a cancelled workflow instead of
holding the replacement run behind an `always()` job.

Strict PR CI run `33292521979` on implementation head
`b4b6b1d0eee46c7b315fbbbbea91e9e3a1cbf02a` and generated PR merge SHA
`91f988afcbce75b8e5bfeab027c7203d30f3f81f` completed with all 38 producers and
the compiled `PR required verdict` green. Protobuf run `33292521965`, Process
Membership run `33292521978` and CodeQL run `33292521969` were also green on the
same head. That evidence covers all six repository shards, all 12 review
mutation shards, all eight lifecycle assemblies with three sequential
iterations, all Release/Debug lanes, all six aria2 RIDs, package audit and
semantic aggregation.

A documentation-only follow-up head `d70522d77052fde222dfe44776dc2b30f562eded`
then exposed a real duplicate-execution defect in run `33293271874`: all eight
assembly shards repeated the same global forensics gate self-tests, and the
Domain copy exhausted the parent budget in the dotnet-stack attach-stall proof.
The final verdict rejected the failed upstream as designed. The topology now
names one gate authority assembly; the other seven shards retain all assembly
phases and iterations but cannot emit global gate evidence. This removes an
exact duplicate rather than reducing a proof, and leaves release Rehearsal
semantics unchanged.

Run `33294254940` then exercised that correction on head
`7563dd5622a0c8b3d1bd5f2b9c81ccc8e626fb5e` and generated merge SHA
`5b6c2dea7ac340488973a6b9c9977930cf56f2dc`. The Architecture authority and six
other lifecycle producers passed; the non-authority reports contained all 18
expected assembly-phase results and no gate results. The Application producer
alone failed when iteration-1 `load` completed successfully in 5.548 seconds
but crossed the slow threshold before evidence capture, producing
`SlowEvidenceMissing / process-exited-before-capture`. The final verdict
rejected the failed upstream. This is the independent Stage 5 slow-evidence
contract, not a missing/duplicate shard or gate-authority failure, so this
checkpoint does not absorb it.

Because this isolation PR targets the Stage 5 branch rather than `main`, the
existing `Build` workflow pull-request branch filter did not trigger on PR #203.
Its ordinary-PR skip and release/Rehearsal preservation contracts are covered
by actionlint and Architecture behavior tests, but require a natural PR run
after the post-Stage-5 rebase/retarget. This limit is not reported as remote
Build evidence.

## Measured Result

The optimized topology is materially faster than the old 668-second stable
first-attempt lifecycle critical job, but it does not meet the requested budget.
The latest gate-authority run had 38 producer durations from 52 to 365 seconds;
median was 130 seconds and the per-job p95 was 305 seconds. Maximum runner
queue/provisioning delay was 204 seconds. The last producer completed 569
seconds after workflow creation and the 41-second fail-closed verdict completed
at 612 seconds (10 minutes 12 seconds).

Ignoring queue delay, the slowest producer plus the required verdict is 406
seconds (6 minutes 46 seconds), a 39.2 percent reduction from the former
668-second critical job but still above both the four-minute repository budget
and five-minute goal. `DownKyi.Windows.Tests` remained the longest producer at
365 seconds: 71 seconds to build its exact closure and 238 seconds for the
required three sequential lifecycle iterations. It therefore exceeds the
five-minute goal even without the final verdict. Non-authority lifecycle jobs
that did not hit a Stage 5 slow-evidence failure fell as low as 87 seconds;
runner and test behavior still varied materially.

Only one earlier fully green exact-head topology run exists, and the corrected
gate-authority run is red for the independent Stage 5 result above, so no
statistically stable cross-run median or p95 can be claimed. The per-job
distribution is a diagnostic sample, not a hosted-runner SLO measurement.
Meeting the four-minute repository budget now requires reducing the Stage 5
lifecycle execution cost or changing its evidence contract; this checkpoint is
not authorized to do either while Stage 5 is under final review. It therefore
remains a validated independent checkpoint rather than a completed performance
closure.

## Completion And Rollback

Completion requires repeated exact ending HEADs with every naturally triggered
workflow green, the final semantic verdict green, a statistically meaningful
hosted-runner p95 at or below five minutes and a repository-controlled critical
path at or below four minutes. No failed run is made green by rerun. The current
checkpoint satisfies correctness on one earlier exact-head run, while its
latest head remains red for an external Stage 5 lifecycle result and does not
satisfy the performance or sampling conditions.

Rollback is one revert of this independent checkpoint. It restores the former
single-lane quality workflow and central-runner sequential solution behavior.
Do not roll back any Stage 5 commit or the release Rehearsal matrix. Integration
must rebase or cherry-pick only after Stage 5 closure and explicitly review the
six known overlapping files named in the Boundary section without reopening
their ownership decisions.

The repository currently has no active main-branch protection or enabled
ruleset requiring a named status check. Before integration, repository settings
must designate the final `PR required verdict` job (and the other independent
security/tooling workflows as policy requires); workflow code alone cannot
establish merge protection.
