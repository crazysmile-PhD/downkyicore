# PR #197 Stage 5 Central Test Runner

Status: stopped at the Stage 5 scope boundary after exact-head native evidence;
implementation checkpoint open. Required CI is not all green and same-head
review is not authorized.

Starting authority:
`dd6364b7e713d3b3c81efd739821cc7e0baafe86` on
`fix/pr196-review-followup`. Stage 2, Stage 3, Stage 4A and Stage 4 are closed
inputs and are not reopened here. Stage 6, merge, release and tag movement are
out of scope.

## Objective

Keep test policy, authorization, canonical invocation and result semantics in
the central runner while moving raw test-child lifecycle correctness to the
existing compiled process-supervision boundary. There is no second lease and no
fallback to the replaced PowerShell path.

## Owner Migration

| Correctness concern | Before Stage 5 | After Stage 5 |
| --- | --- | --- |
| Test policy | PowerShell central runner | `CentralTestPolicy` |
| Canonical argv | PowerShell central runner | `CentralTestOrchestrator` |
| Authorization issuer | PowerShell central runner | `CentralTestAuthorization` |
| Authorization transport | PowerShell anonymous pipe plus inherited numeric handle string | Random current-user named endpoint carried in immutable `LaunchSpec` environment |
| Authorization verifier | `CentralTestExecutionGuard` | `CentralTestExecutionGuard` |
| Process launch | PowerShell raw `Process` | `OwnedProcessLease` / `SupervisorHost` |
| Process ownership | PowerShell child reference and manual cleanup | `OwnedProcessLease` platform containment and membership authority |
| Deadline | PowerShell `Stopwatch` plus independent cleanup allowance | One caller-created `TransitionBudget` consumed by authorization and lease |
| Terminate/reap | PowerShell kill/wait helpers | `OwnedProcessLease` / `SupervisorHost` |
| Streams | PowerShell async process stream handlers | `OwnedProcessLease` typed outcome after bounded drain |
| TRX semantics | PowerShell validator helpers | `CentralTestExecutionValidator` |
| Aggregate result | PowerShell project/solution functions | `CentralTestOrchestrator` typed project/solution results |

The target table has one correctness owner per concern. `SupervisorHost` is an
opaque launch/ownership transport; it is not a test policy, authorization or
TRX owner. PowerShell and YAML are invocation glue only.

## Authorization Transport

The baseline anonymous pipe required an inherited process-local HANDLE/fd
number in `DOWNKYI_CENTRAL_TEST_PIPE`. That representation cannot be copied
through the additional supervisor process as an ordinary string.

Stage 5 replaces it with the repository's bounded random `IpcEndpointName` and
a one-client `NamedPipeServerStream` using
`PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`. This has the same
logical protocol on all three supported platforms:

- Windows: the target opens the random current-user named-pipe endpoint;
- Linux: .NET maps the same bounded name to its local named-pipe transport;
- macOS: the 21-character physical name stays within the proved Unix-socket
  path budget.

The immutable launch environment contains the physical endpoint name and a
base64 token. The issuer writes exactly one versioned frame with a 32-byte token
and 32-byte SHA-256 hash of the full canonical argv, then closes the endpoint.
The guard clears endpoint/token/legacy environment values, validates token and
hash in fixed time and requires immediate EOF after the exact frame. Wrong
token, wrong invocation, replay, EOF, partial frames, wrong child and legacy
numeric-handle transport fail closed.

This is a named endpoint, not a copied numeric OS capability. `SupervisorHost`
only applies the immutable environment overlay to the owned target. Architecture
proof rejects any dependency from it to central authorization, the guard or
test-runner policy.

## Process Lifecycle And Deadline

For each test execution, `CentralTestOrchestrator` creates one
`TransitionBudget`, issues authorization for the immutable arguments and calls
`OwnedProcessLease.StartAsync` with one `LaunchSpec`. Authorization connect and
write consume the same budget. `OwnedProcessLease` then owns:

- start and ownership establishment before test code;
- wait and caller cancellation classification;
- termination after timeout, cancellation, owner EOF or supervisor failure;
- authoritative reap and group quiescence;
- standard-input closure, output/error capture and bounded stream drain;
- typed process outcome and cleanup failures.

The runner contains no raw `Process.Start`, `Kill`, `WaitForExit`, process-tree
enumeration, private cleanup state machine or fresh execution deadline. A build
is a separate owned invocation; it also uses `OwnedProcessLease`, but it does
not share or redefine the subsequent test execution's deadline.

Lifecycle-marker write authority is one-shot. The lifecycle script grants
`DOWNKYI_LIFECYCLE_MARKER_OWNER=1` only to the outer execution target. The
module initializer consumes that value before any test can launch a child;
nested test processes inherit no write authority and clear the inherited marker
before fixture construction.

## TRX Boundary

`OwnedProcessLease` answers how the process executed and was collected. It does
not decide whether tests passed. `CentralTestExecutionValidator` remains the
authoritative semantic boundary and requires:

- a present, structurally complete and internally consistent TRX;
- at least one executed test;
- exact expected-class execution when a selection is supplied;
- at least one passed result for every expected class;
- no failed TRX result hidden by process exit code zero;
- a unique expected report when the caller requests selection proof.

Canonical xUnit arguments, class selection, `parallel=none`, platform routing,
runner policy and VSTest rejection are preserved.

## Linux Delegation

`script/invoke-ci-test-action.ps1` now evaluates the existing delegated-cgroup
bootstrap before branching between project and solution mode. It does not
create a central-runner-private containment path. The shared Linux
`OwnedProcessLease` implementation remains the only containment and membership
owner. If the delegated cgroup is unavailable, launch fails closed; there is no
PID, PPID, `/proc` or process-enumeration kill fallback.

The direct `script/test-review-invariants.ps1` entrypoint evaluates the same
shared bootstrap before it loads the runner or starts a normal/mutation test.
This prevents a caller from bypassing the delegated scope merely by invoking
the review corpus directly.

`-NoBuild` applies to the selected test target, not to the compiled provider
required to interpret that request. If `DownKyi.CentralTestRunner.dll` is
missing, the wrapper deterministically builds that provider while preserving
the caller's `-NoRestore` choice, then forwards `NoBuild=true` to the target
options. There is still one compiled entrypoint and no PowerShell test-host
fallback.

The macOS packaged-app launch verifier remains a bounded release-tooling owner
of the app root only and is outside Stage 5. Its TERM-resistance regression
therefore constructs one test-owned shell root with no nested `sleep` process.
The test still proves the existing TERM-to-KILL bound without creating a child
that the root-only verifier cannot reap.

## Executable Proof

Normal authorization and ownership tests prove exact invocation success,
pre-execution ownership, quiescent completion, hung-child termination,
cancellation cleanup, supervisor failure, owner-lifetime EOF and stream drain.
Authorization tests separately prove wrong token, wrong argv hash, replay,
endpoint EOF, partial frame, wrong child and direct execution rejection.

The `central-test-runner-process-ownership` review invariant executes ten
adversarial profiles. Every profile must execute all ten mutation tests and
produce exactly one failed test in its owning invariant:

1. raw `Process.Start` returns to the central runner;
2. a numeric HANDLE/fd string is accepted as authorization transport;
3. `SupervisorHost` becomes an authorization authority;
4. the assembly guard is bypassed;
5. one-shot authorization is replayed;
6. a fresh test-process deadline appears;
7. private terminate/reap ownership returns to the central runner;
8. process exit zero overrides a failed TRX;
9. zero executed tests are accepted;
10. Linux delegation falls back to process enumeration.

The outer review script validates `expectedFailedTests`, so a mutation cannot
pass because of a build error, unrelated exception or multiple broken
invariants.

## Local Validation Checkpoint

Validation before the documentation commit produced:

- strict focused and solution Release builds: zero warnings and zero errors;
- authorization tests: 8/8 passed;
- affected central-runner/ownership/guard tests: 25/25 passed;
- TRX behavior tests: 15/15 passed;
- full Architecture before the marker follow-up: 352/352 passed;
- post-follow-up full Architecture execution inside lifecycle sanity: 353/353
  passed;
- review-invariant gate: 13 invariants, seven normal projects, 366 normal tests
  and 25 adversarial proofs passed;
- all ten Stage 5 mutation profiles: ten tests executed with exactly one owning
  failure per profile;
- lifecycle ownership audit: 657 matches and zero violations;
- one-assembly lifecycle sanity: one assembly, six phases, zero failures, zero
  residual children and slow evidence 1 captured / 0 missing; the marker held
  only the outer PID for started, disposing and disposed;
- PowerShell syntax, JSON parsing, platform selector, formatting verification
  and `git diff --check` passed.

The Windows routed suite selected eight projects. The first seven project
reports were green; the existing aria2 real-binary integration remained skipped
without `DOWNKYI_ARIA2_BINARY`. `DownKyi.Windows.Tests` executed 91 tests with
90 passed and one failure in
`OwnedProcessLeasePlatformTests.CallerCancellationStillCompletesOwnedCleanupBeforePropagating`.
The failure was a sharing violation while the pre-existing Stage 2 fixture read
its readiness JSON immediately after a file watcher notification. It did not
show a Stage 5 runner child, authorization, containment, deadline, reap, drain
or residual-process failure. The run was not blindly rerun and the closed Stage
2 fixture was not changed.

Linux and macOS cross-platform compilation is not native execution evidence.
Their runner, transport and containment proof remains pending naturally
triggered exact-head CI.

## First Exact-Head CI Feedback

The documentation checkpoint produced exact head
`5177d4605e12fbeb0039e8ef27434d8062938051` and naturally triggered Strict PR
run `33232585457`. Format, package audit, CodeQL, protobuf, FFmpeg, delegated
Linux cgroup and both macOS membership jobs passed. No job was rerun and no
same-head `@codex review` was requested because the required set was not green.

The failed jobs established four Stage 5 follow-up causes:

1. Ubuntu Strict entered the review corpus directly, outside the shared
   delegated-cgroup bootstrap, and failed to create its lease cgroup under the
   hosted service scope.
2. All six aria2 TLS jobs restored the solution and built only
   `DownKyi.Tests`; `-NoBuild` incorrectly prevented bootstrap of the missing
   compiled runner provider before the prebuilt target could execute.
3. Windows Strict reached the Architecture policy gate, where two budget
   assertions depended on LF source text and rejected the hosted Windows CRLF
   checkout.
4. macOS Strict completed `DownKyi.MacOS.Tests` with a 93/93 passing TRX, then
   the outer lease correctly reported `OwnedTreeNotQuiescent`. The last
   process-spawning fixture killed only its term-resistant app root and left the
   shell's `sleep` descendant alive.

Implementation/proof follow-up
`86d99537a8a360edce00be16d666f96c3dda93c1` routes the direct review gate through
the shared delegated scope, bootstraps the provider independently of target
`NoBuild` and normalizes the static C# oracle's line endings. It also attempted
process-group cleanup in the macOS release verifier; the next exact-head run
proved that attempt did not resolve the residual, and the later in-scope
follow-up removes it. The closed Stage 2, Stage 3, Stage 4A and Stage 4
production contracts remain unchanged.

The Assembly Lifecycle job failed earlier in the unchanged Stage 3 slow-
evidence ordering self-test with `AggregateException`, before formal assembly
phases and with repository-test authorization disabled for that fixture. The
Stage 5 follow-up does not alter that observer/collector contract. Replacement
exact-head CI must determine whether this remains a separate blocker; blind
rerun or a Stage 3 scope expansion is prohibited.

## Follow-Up Local Validation

Validation of `86d99537a8a360edce00be16d666f96c3dda93c1` before the follow-up
documentation commit produced:

- targeted policy/release/mutation proof: 38/38 passed on the Windows CRLF
  checkout;
- full Architecture: 354/354 passed;
- review-invariant gate: 13 invariants, seven normal projects, 368 normal tests
  and 25 adversarial proofs passed;
- all ten Stage 5 profiles: ten tests executed with exactly one owning failure
  per profile;
- strict Release solution build: zero warnings and zero errors;
- `dotnet format --verify-no-changes`: 0/1008 files changed;
- PowerShell parse, `bash -n`, JSON-backed corpus execution and
  `git diff --check` passed.

An isolated missing-Debug-provider check proved that `-NoBuild -NoRestore`
bootstrapped `DownKyi.CentralTestRunner` with zero build warnings/errors while
leaving the target unbuilt. The deliberately stale Debug target execution did
not complete and was canceled; it is not counted as test evidence. The newly
built Release target and all reported Release gates above are the local
executable evidence. Native Linux delegation and macOS cleanup remain CI-only
proof.

## Second Exact-Head CI Feedback

Follow-up documentation head
`83d62b8ad39c6ca0aaeaaabca595d76077191a59` naturally triggered Strict PR run
`33234070529`. Windows Strict, Ubuntu Strict, all six aria2 TLS jobs, format,
package audit, CodeQL, protobuf, FFmpeg and the three platform-membership jobs
passed. This proves the CRLF, direct Linux delegation and provider-bootstrap
fixes on their native paths.

macOS Strict again completed `DownKyi.MacOS.Tests` with a 93/93 passing TRX and
then returned `OwnedTreeNotQuiescent` after consuming the process deadline.
Therefore the release-script process-group attempt was neither closure evidence
nor an allowed Stage 5 change. Commit
`4691e100826c760a0578568242ffa3350bca14df` restores the release script and its
architecture assertions exactly, then changes only the TERM-resistant test app
from a shell root that repeatedly spawns `sleep` to one single shell root. The
outer native lease remains responsible for proving quiescence.

The same run repeated the independent Assembly Lifecycle Stage 3 self-test
failure: `AggregateException`, all configured/mutation phase results null,
formal phases not started, and ownership audit 657 matches / zero violations.
No prior run was rerun, no Stage 3 code was modified and no same-head review was
requested.

Local validation of `4691e100826c760a0578568242ffa3350bca14df` produced:

- focused runner/release fixture policy: 29/29 passed;
- full Architecture: 355/355 passed;
- review-invariant gate: 13 invariants, seven normal projects, 369 normal tests
  and 25 adversarial proofs passed;
- all ten Stage 5 profiles still executed ten tests with exactly one owning
  failure per profile;
- strict Release solution build: zero warnings and zero errors;
- format verification, staged LF `bash -n` and `git diff --check` passed.

The single-root fixture is cross-compiled but cannot execute on Windows. Its
behavioral proof remains the next naturally triggered macOS Strict run.

## Third Exact-Head CI Feedback And Residual Diagnostics

Documentation head `86f9ac51c73856b1734337872fed6e22456748ba` naturally
triggered Strict PR run `33235099993`. Windows Strict, Ubuntu Strict, all six
aria2 TLS jobs, format, package audit, CodeQL, protobuf, FFmpeg and all three
platform-membership jobs passed. macOS Strict nevertheless completed the same
93/93 passing TRX and then returned `OwnedTreeNotQuiescent`. This disproves the
single nested `sleep` explanation; it does not authorize a change to the closed
Stage 4 contract or the unchanged release verifier.

The same run repeated the separate Stage 3 slow-evidence ordering self-test
`AggregateException` for the third exact head, before any formal assembly phase
started. No failed job was rerun, no Stage 3 or Stage 4 code was modified and no
same-head review was requested.

Implementation/proof follow-up
`3a909f22e465da94d91c09964c582b84478d0ee0` closes only the Stage 5
observability gap. `CentralTestOrchestrator` now writes the typed lease failure's
already captured stdout and stderr before preserving the same exception. A
macOS assembly-end observer lists unexpected process-group members as JSON. The
observer neither terminates nor reaps a process and cannot decide success;
`OwnedProcessLease` remains the sole membership, quiescence and cleanup owner.

Local validation of that diagnostic follow-up produced:

- focused central-runner architecture proof: 13/13 passed;
- authoritative documentation/index proof: 12/12 passed;
- full Architecture: 356/356 passed;
- review-invariant gate: 13 invariants, seven normal projects, 370 normal tests
  and 25 adversarial proofs passed;
- all ten Stage 5 profiles still executed ten tests with exactly one owning
  failure per profile;
- strict macOS-project, Architecture-project and solution Release builds: zero
  warnings and zero errors;
- format verification and `git diff --check` passed.

At that local checkpoint the observer was cross-compiled only on Windows. The
next naturally triggered macOS Strict run supplied its native identity evidence
below.

## Fourth Exact-Head CI Feedback And Stop Condition

Diagnostic documentation head `636d276b6aff7663ab1836fd8357e6713062adfb`
naturally triggered Strict PR run `33236158868`. Windows Strict, Ubuntu Strict,
format, package audit and all six aria2 TLS jobs passed. The separate Build,
CodeQL, Protobuf and three-platform Process Membership Feasibility workflows
also passed on that exact head. No run was rerun.

macOS Strict passed its build, Architecture and review-invariant steps, then the
repository test gate failed in `DownKyi.MacOS.Tests`. The diagnostic observer
reported one unexpected anchored-group member:

```json
{"processGroupId":8342,"testProcessId":8343,"unexpected":[{"processId":9191,"processName":"VBCSCompiler"}]}
```

This replaces the earlier shell/`sleep` hypothesis with compiler-server
identity evidence. The only `dotnet` build/publish call site inside the macOS
test assembly is the bundle-layout release fixture's `dotnet publish`; the log
does not retain the residual process's parent chain, so that call-site link is
an inventory-based classification rather than captured parentage. Release
tooling and macOS signing are outside Stage 5, and the ordinary lease correctly
failed closed instead of treating the residual as quiescent. Stage 5 therefore
cannot modify that fixture or weaken membership authority.

The Assembly Lifecycle job independently repeated the unchanged Stage 3 slow-
evidence ordering self-test `AggregateException`: configured and mutation
results were null, all readiness/real-lifecycle-path contract checks were false,
formal phases did not start, and the ownership audit still reported 657 matches
with zero violations. Stage 3 observer, collector and slow-evidence policy are
closed inputs. This fourth repetition is a required-CI blocker but does not
authorize a Stage 5 fix.

The exact-head required set is therefore red for two prohibited scope
expansions: the macOS bundle/release fixture's compiler-server residual and the
unchanged Stage 3 self-test. No `@codex review` was requested. Stage 5 remains
an open implementation checkpoint and Stage 6 remains deferred.

## Commits And Closure

- `a768a9d86bba3b5bd0f0834a7997cf21a9ccd017` — compiled migration and
  executable proof;
- `8ecba8f0fc86dd10d70ce309c9101379873a8ba6` — nested lifecycle-marker
  isolation proof;
- `5177d4605e12fbeb0039e8ef27434d8062938051` — initial documentation
  checkpoint;
- `86d99537a8a360edce00be16d666f96c3dda93c1` — native-CI ownership follow-up;
- `4691e100826c760a0578568242ffa3350bca14df` — in-scope single-root macOS
  TERM-resistance fixture and release-tooling reversion;
- `3a909f22e465da94d91c09964c582b84478d0ee0` — typed failure-output
  propagation and diagnostics-only macOS residual-member observer;
- `636d276b6aff7663ab1836fd8357e6713062adfb` — residual-diagnostics
  documentation checkpoint;
- stop-condition documentation checkpoint — this file and the parent execution
  plan.

After the stop-condition documentation commit, push once and verify local HEAD,
upstream, remote branch and PR #197 head are identical. Do not rerun a previous
workflow. Windows, Linux and macOS runner proof, authorization transport, Stage
5 mutations and required lifecycle checks must all be green on one exact head
before requesting `@codex review`. Any later authorized fix creates a new head
and invalidates prior CI/review closure.

Stage 5 is `CLOSED` only when required exact-head CI is green, the same-head
review is clean, no in-scope blocking thread remains and the worktree is clean.
Until then its status is `implementation checkpoint open`. Stage 6 must not
start automatically.

## Rollback

Revert the Stage 5 implementation/proof commits and their documentation as one
checkpoint if rollback is required. Do not restore the anonymous-pipe/raw
PowerShell owner as a fallback beside the compiled path, do not weaken the
assembly guard, and do not alter the closed Stage 2/3/4 contracts as part of a
Stage 5 rollback.
