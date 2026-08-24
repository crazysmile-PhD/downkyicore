# Testing

Test project inventory and OS ownership are not maintained in this file. Query
the `*.Tests.csproj` files: every runnable project declares an explicit
`DownKyiTestPlatforms` subset of `Windows;Linux;macOS`.

[test-solution.ps1](../../script/test-solution.ps1) discovers the complete
inventory, validates those declarations and selects projects owned by the
current OS. [test-project.ps1](../../script/test-project.ps1) is the direct
project boundary; the compiled central runner applies policy and result
semantics from [test-runner-policy.json](test-runner-policy.json). Direct
solution-wide `dotnet test` is not the canonical repository entry point.

## Stable Test Policy

- Native behavioral tests belong in the corresponding OS-owned project.
  Architecture tests verify ownership and workflow wiring; they do not emulate
  another OS's native behavior.
- Platform-adaptive contract tests may remain cross-platform when every OS
  executes a real assertion rather than skipping the complete behavior.
- Tests use isolated repository-controlled data roots. They do not read real
  settings, cookies, download databases or aria2 sessions.
- Network contract tests use fixtures or loopback servers. Live probes are
  separately authorized operational evidence, not CI tests.
- A deterministic behavior or protocol regression runs in PR CI. Repeated
  race, process, GC, real-binary and heavy platform evidence uses the existing
  Main/rehearsal owner unless a security policy requires PR coverage.

## Authorities

- Module-boundary rationale:
  [module-boundary-ratchets.md](module-boundary-ratchets.md).
- Lifecycle measurement semantics:
  [assembly-lifecycle-stability.md](assembly-lifecycle-stability.md).
- Lifecycle machine ownership:
  [assembly-lifecycle-owners.json](assembly-lifecycle-owners.json).
- Review methodology:
  [review-invariant-policy.md](review-invariant-policy.md).
- Executable invariant mapping:
  [review-invariant-corpus.json](review-invariant-corpus.json).
- Test routing and runner policy:
  [test-runner-policy.json](test-runner-policy.json).
- Process, authorization and deadline ownership intent:
  [process-lifecycle-ownership.md](../design-docs/process-lifecycle-ownership.md).
- Canonical formal commands:
  [verification-and-rollback.md](../operations/verification-and-rollback.md).

Do not copy project lists, test counts, runner matrices or proof results into
this index. Query the owners or their generated CI artifacts when needed.
