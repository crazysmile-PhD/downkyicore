# CI Adversarial Validation

## Purpose and safety boundary

CI adversarial validation answers a narrow defensive question: would the
repository's own checks reject a realistic production regression or a disabled
guard? Perform it only in a local clone, an isolated work branch, or a
sandboxed test environment. Do not probe external accounts, services,
infrastructure, or systems outside the repository.

Every fault injection must have an identifier, exact file and line-level
change, expected outcome, observed outcome, command evidence, and an immediate
revert. Confirm that the mutation has no remaining diff before proceeding to
the next case. Permanent remediation belongs on a separate work branch and
must never modify the production branch directly.

## Method

1. Inspect the repository, history, CI definitions, tests, documentation and
   recorded issues or pull requests. Capture the default branch, baseline SHA,
   clean-tree state, local toolchain, and hosted CI matrix.
2. Model production invariants and map each to its executing CI guard,
   platform, trigger, and evidence artifact. A test name or a command string is
   not evidence that the guard actually runs.
3. Introduce one reversible local fault at a time. Favor representative changes
   to lifecycle ordering, error classification, validation conditions, test
   selection, and workflow-step execution.
4. Run the relevant gate, record the observed result, revert the fault, and
   verify the reversion before attempting another fault.
5. Classify each result and generalize the root cause. Do not claim a platform,
   trigger, or runner was covered unless the relevant gate actually executed.

| Classification | Meaning |
| --- | --- |
| `DETECTED` | The intended guard failed for the injected regression. |
| `NOT DETECTED` | The applicable guard completed successfully despite the regression. |
| `PARTIALLY COVERED` | A related guard exists, but it can be bypassed or does not prove the invariant. |
| `UNTESTED` | No applicable guard was found or exercised. |
| `UNKNOWN` | Evidence is insufficient; this is not a passing result. |

## Guard design rules

- Test the actual workflow structure and behavior. A CI self-test must identify
  the intended job and executable step; it must not accept a required command
  merely because it appears in a comment, documentation, or unrelated job.
- Keep deterministic semantic regressions in the PR corpus. The corpus is a
  targeted ratchet, not a substitute for the full test suite.
- Make test selection observable: require a test result for every selected
  project or class, preserve per-assembly results, and fail on missing reports.
- Treat workflow parsing, formatting, build, architecture checks, package
  audits, semantic tests, lifecycle stress, and real-binary checks as separate
  guards with explicit trigger and platform ownership.
- Add a test-of-tests whenever a new structural CI guard is introduced. The
  negative fixture should prove that a disabled, commented, renamed, or
  conditionally skipped guard is rejected.
- Record runner-only, platform-specific, and destructive-path scenarios
  separately. Local success cannot establish hosted Windows, Linux, macOS,
  scheduled-main, or rehearsal coverage.

## Evidence and remediation

An audit report must describe the violated invariant, existing guard, mutation,
expected and actual result, escape mechanism, root cause, minimal reproduction,
false-positive risk, platform scope, and generalized remediation. Group related
symptoms by shared root cause rather than opening an issue per symptom.

Prioritize remediation that restores execution of the broadest guard first,
then add targeted invariant coverage, then add longer-running or
platform-specific evidence. Re-run the mutation after remediation to prove the
new guard detects it. A green check is only evidence for the checks that ran;
`UNKNOWN` remains open until independently resolved.
