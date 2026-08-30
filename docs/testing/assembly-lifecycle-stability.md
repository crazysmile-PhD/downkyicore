# Assembly Lifecycle Stability

## Why The Gate Exists

Repository tests load assemblies, discover and execute tests, tear down fixture
state, then exit their process tree. A normal test assertion can pass while a
thread, child process, diagnostic collector, fixture directory or redirected
stream remains alive. The lifecycle gate makes those completion conditions
explicit and fails closed.

## Single Process Owner

Every phase is launched through `OwnedProcessLease`. The OS-specific ownership
primitive is a Windows Job Object, Linux delegated cgroup or authoritative
macOS process-group membership. See
[process-lifecycle-ownership.md](../design-docs/process-lifecycle-ownership.md)
for the ownership and deadline contract.

## Six Phases

For every selected assembly and iteration the gate records exactly these
results:

1. `load`
2. `assembly-info`
3. `discovery`
4. `execution`
5. `assembly-teardown`
6. `process-exit`

A phase passes only when its protocol/output contract succeeds and the owned
process tree reaches bounded quiescence. Assembly teardown additionally
requires the fixture marker and isolated data root cleanup.

## Profiles

The profile-to-iteration mapping is machine-owned by
`script/test-assembly-lifecycle.ps1`.

- `Local` is the smallest developer check.
- `PR` is the required fast lifecycle signal.
- `Main` adds a small amount of post-merge repetition.
- `Rehearsal` is release-readiness stress, not normal PR feedback.
- `Flaky` is reserved for a dedicated nondeterminism investigation.

Do not duplicate numeric profile values in another document or workflow; pass
the profile name.

## Failure Report

The JSON and Markdown report are stored below the requested results directory.
Every failed phase is also printed directly to the CI log with:

```text
Assembly:
Iteration:
Phase:
FailureKind:
PrimaryFailure:
TargetExitCode:
OwnedTreeQuiescent:
CleanupFailures:
Stdout:
Stderr:
EvidencePath:
LikelyOwner:
```

`LikelyOwner` is derived only from typed failure classification:
process/quiescence failures map to `OwnedProcessLease`, diagnostic capture
failures to `ForensicsCollector`, teardown failures to `FixtureTeardown`, and
phase protocol/output failures to `LifecycleProbe`.

Primary and cleanup failures are separate. A cleanup problem must not replace
the operation's first causal failure. Expensive evidence is collected only for
failure, timeout, slow-threshold or residual-process paths.

## Run Locally

Build once, then run the small lifecycle profile:

```powershell
dotnet restore ./DownKyi.sln
dotnet build ./DownKyi.sln -c Release --no-restore `
  -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Profile PR `
  -NoBuild `
  -ResultsDirectory ./artifacts/assembly-lifecycle/local-pr
```

Use `-AssemblyPattern <name>` for a focused iteration. The script still uses
the central test authorization boundary; it is not a bypass around repository
test policy.

## Release Rehearsal

The release workflow runs one unsharded `Rehearsal` job for a release candidate
or explicitly requested release-readiness run. The equivalent local command is:

```powershell
pwsh ./script/test-assembly-lifecycle.ps1 `
  -Configuration Release `
  -Profile Rehearsal `
  -NoBuild `
  -ResultsDirectory ./artifacts/assembly-lifecycle/rehearsal
```

Preserve the generated report and demand-driven evidence for the exact commit,
OS, architecture and dirty-worktree state shown in the report.
