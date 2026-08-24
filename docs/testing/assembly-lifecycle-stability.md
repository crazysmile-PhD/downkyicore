# Assembly Lifecycle Stability Gate

Status: required quality and release gate

## Purpose

A green assertion summary does not prove that a test executable loaded cleanly,
preserved its runner protocol, disposed fixtures, stopped owned work or exited
deterministically. The lifecycle gate therefore runs each selected xUnit
assembly behind the repository process owner and measures lifecycle boundaries
separately.

The central runner owns project selection, canonical xUnit invocation,
one-shot test authorization and TRX semantics. The shared process lease owns
child start, containment, wait, termination, reap, quiescence, streams and the
single caller-created transition budget. Neither owner may duplicate the other.

## Executable Owners

- Dynamic probe and report schema:
  [test-assembly-lifecycle.ps1](../../script/test-assembly-lifecycle.ps1).
- Static lifecycle inventory:
  [audit-lifecycle-ownership.ps1](../../script/audit-lifecycle-ownership.ps1).
- Machine-readable start/stop/teardown declarations:
  [assembly-lifecycle-owners.json](assembly-lifecycle-owners.json).
- Probe child:
  [DownKyi.AssemblyLifecycleProbe](../../tools/DownKyi.AssemblyLifecycleProbe/).
- Process owner: [DownKyi.ProcessSupervision](../../tools/DownKyi.ProcessSupervision/).
- CI/release profiles: [quality.yml](../../.github/workflows/quality.yml) and
  [build.yml](../../.github/workflows/build.yml).
- Human invocation: [verification-and-rollback.md](../operations/verification-and-rollback.md).
- Design intent: [process-lifecycle-ownership.md](../design-docs/process-lifecycle-ownership.md).

Profile counts, thresholds, capture lead, quiescence duration and report fields
are owned by the script and generated machine report. Do not copy their current
numeric values into downstream policy documents.

## Dynamic Phases

Each phase has an independent child-process boundary:

1. `load`: load through a collectible `AssemblyLoadContext`, unload and prove
   the context is no longer rooted.
2. `assembly-info`: require exactly one valid xUnit metadata object.
3. `discovery`: require exactly one valid test-discovery array.
4. `execution`: require valid automated reporter output and a successful test
   outcome.
5. `assembly-teardown`: require the fixture lifecycle marker sequence and
   removal of its process-specific data root.
6. `process-exit`: require bounded exit and authoritative owned-tree
   quiescence without protocol pollution.

xUnit automated reporting uses the single reporter mode enforced by the probe.
The child stdin is redirected and closed so reporter completion observes
deterministic EOF rather than an interactive terminal. A mutation self-test
must prove unsupported reporter arguments are rejected by that same launch
validator.

## Measurement Semantics

- Process-phase duration begins before process creation and ends at the process
  owner's monotonic target-exit transition. Execution therefore includes test,
  fixture and runner shutdown.
- Assembly teardown is the fixture marker interval. Report serialization,
  collector work and caller-side failure mapping are not child-lifetime time.
- Slow classification and proactive evidence capture are distinct. Evidence
  collection must begin before the classification boundary without silently
  lowering it.
- Diagnostic collection can perturb a live process. The report preserves
  instrumented duration, collector duration and observer state separately.
- Process-owner failure, forensics failure and cleanup failure remain distinct;
  later evidence or cleanup cannot replace the first causal transition.
- Execution, timeout and post-teardown evidence are separate; one phase cannot
  inherit another phase's evidence.
- Reports identify runtime, OS, architecture, exact commit, dirty state,
  profile, iteration count and thresholds. Cross-machine timing comparisons are
  invalid without compatible metadata and datasets.

## Ownership And Residual Work

The static audit detects process/thread/timer/dispatcher/Host/global-event and
synchronous-wait mechanisms. Every match must resolve through the most specific
entry in [assembly-lifecycle-owners.json](assembly-lifecycle-owners.json), with
an explicit starter, stopper and teardown sequence. The machine inventory is an
ownership policy, not a broad suppression list.

Residual truth comes from the platform ownership primitive: Windows Job Object
active-process state, delegated Linux cgroup membership or anchored macOS
libproc process-group membership. PID, PPID, process name, creation time and
command line are diagnostic evidence only; they cannot authorize cleanup or
convert a non-quiescent owned tree to success.

Forensics is an observer, not a process owner. It may capture sanitized identity,
thread, tree and managed-stack evidence, but it cannot kill, reap, extend child
lifetime, create a deadline or override the lease verdict. Collector execution
uses an attenuated window from the same transition budget and preserves primary
and cleanup failures independently.

## Forensics Self-Defense

Formal profiles run the existing forensics self-tests through the same owner
paths as real phases. They prove that:

- slow evidence is captured before classification and before authoritative
  target exit;
- an evidence hold is one-shot, lease-owned and acknowledged by the held
  target rather than inferred from observer output;
- collector start, terminate, reap and stream drain consume the caller's
  existing monotonic budget without a replacement deadline;
- a temporarily locked marker is retried and parsed after release, while
  non-contention I/O and access errors retain a distinct classification;
- a persistent descendant fails authoritative quiescence even when observation
  misses it or the parent identity changes;
- observer, persistence and cleanup failures remain visible beside the causal
  process-owner failure;
- private paths, URLs, cookies and command-line secrets are redacted;
- inconsistent nominally-passed proof objects and intentional owner mutations
  fail closed.

The generated report is the authority for its schema, detailed predicates and
current values. Do not copy example JSON, field inventories, run IDs or proof
counts into this document.

## Completion And Rollback

A lifecycle fix requires a deterministic owner/teardown regression, focused
stability evidence, normal repository tests and the required CI/release
profiles. A faster or green rerun does not replace causal ownership proof.

Rollback the probe, process owner, scripts, machine policy, tests and workflow
wiring as one coherent change. Never retain references to a removed phase or
replace a failed lifecycle gate with retries, PID heuristics or suppression.
